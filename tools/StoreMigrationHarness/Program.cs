using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// Store v2 M3 failure matrix — exercises the REAL MPStoreMigration code (via
// reflection into the mod DLL) against synthetic and real-fixture stores.
// Every case builds its own scratch store, runs the migrator, and asserts the
// resulting disk state. Exit code = number of failed cases.
namespace StoreMigrationHarness
{
    internal sealed class ConsoleLogger : BAModAPI.IModLogger
    {
        public void Info(string message)  => Console.WriteLine("  [i] " + message);
        public void Warn(string message)  => Console.WriteLine("  [W] " + message);
        public void Error(string message) => Console.WriteLine("  [E] " + message);
        public void Error(Exception ex)   => Console.WriteLine("  [E] " + ex);
    }

    internal static class Program
    {
        const string Marker  = "storeformat.bamp.json";   // = MPSaveManager.StoreFormatMarkerName
        const string Journal = "_migrate_journal.json";
        const string UndoMap = "_undo_map.json";

        static MethodInfo _migrate, _resume;
        static string _work;
        static int _failed;

        static void Main()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (var dir in new[] {
                    @"C:\Program Files (x86)\Steam\steamapps\common\Big Ambitions\Big Ambitions_Data\Managed\",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory) })
                {
                    string p = Path.Combine(dir, name);
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }
                return null;
            };
            Run();
            Environment.Exit(_failed);
        }

        static void Run()
        {
            // Route the mod's Plugin.Logger to the console (internal setter).
            var pluginType = Type.GetType("BigAmbitionsMP.Plugin, BigAmbitionsMP");
            var modLogType = Type.GetType("BigAmbitionsMP.ModLog, BigAmbitionsMP");
            var log = Activator.CreateInstance(modLogType, new object[] { new ConsoleLogger() });
            pluginType.GetProperty("Logger").GetSetMethod(true).Invoke(null, new[] { log });

            var mig = Type.GetType("BigAmbitionsMP.MPStoreMigration, BigAmbitionsMP");
            _migrate = mig.GetMethod("Migrate", BindingFlags.NonPublic | BindingFlags.Static);
            _resume  = mig.GetMethod("ResumePendingJournal", BindingFlags.NonPublic | BindingFlags.Static);

            _work = Path.Combine(Path.GetTempPath(), "bamp-m3-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_work);
            Console.WriteLine($"work dir: {_work}\n");

            Case_Happy();
            Case_Idempotent();
            Case_StalePreAdoptionJournal();
            Case_ResumePartialRenames();
            Case_ResumeFinalizeOnly();
            Case_AnomalyBothExist();
            Case_AnomalyNeitherExist();
            Case_EmptyStore();
            Case_LockedFileThenResume();
            Case_HexNamedSession();
            Case_RealFixtureFullScale();

            Console.WriteLine($"\n==== {(_failed == 0 ? "ALL PASS" : _failed + " FAILED")} ====");
        }

        // ── fixture building ─────────────────────────────────────────────────

        static string NewStore(string name)
        {
            string root = Path.Combine(_work, name);
            Directory.CreateDirectory(root);
            return root;
        }

        static void AddSession(string root, string name, string pid, long savedAt, bool manifest = true, int chars = 1)
        {
            string d = Path.Combine(root, name);
            Directory.CreateDirectory(d);
            for (int i = 0; i < chars; i++)
            {
                string c = Path.Combine(d, $"guid-{i:D32}");
                Directory.CreateDirectory(c);
                File.WriteAllText(Path.Combine(c, "save.hsg"), $"HSG {name} {i} {new string('x', 64)}");
            }
            if (manifest)
                File.WriteAllText(Path.Combine(d, "manifest.bamp.json"),
                    Newtonsoft.Json.JsonConvert.SerializeObject(new { PlaythroughId = pid ?? "", SavedAtUnix = savedAt, Slots = new object[0] }));
            // Backdate the folder — timestamp preservation is asserted later.
            Directory.SetLastWriteTimeUtc(d, new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc).AddDays(savedAt % 30));
        }

        static void Migrate(string root) => _migrate.Invoke(null, new object[] { root, Path.Combine(root, Marker) });
        static void Resume(string root)  => _resume.Invoke(null, new object[] { root });

        // ── assertions ───────────────────────────────────────────────────────

        static void Check(string caseName, bool ok, string what)
        {
            if (!ok) { _failed++; Console.WriteLine($"  FAIL [{caseName}] {what}"); }
        }

        static List<string> SessionsAtDepth2(string root) =>
            Directory.GetDirectories(root).Where(p => !Path.GetFileName(p).StartsWith("_"))
                .SelectMany(Directory.GetDirectories).Select(p => Path.GetFileName(p)).OrderBy(x => x).ToList();

        static List<string> FlatLeftovers(string root) =>
            Directory.GetDirectories(root).Select(Path.GetFileName)
                .Where(n => !n.StartsWith("_") && Directory.Exists(Path.Combine(root, n)) &&
                            (File.Exists(Path.Combine(root, n, "manifest.bamp.json")) ||
                             Directory.GetDirectories(Path.Combine(root, n)).Any(c => Path.GetFileName(c).StartsWith("guid-"))))
                .ToList();

        static string PidOf(string root, string session) =>
            Directory.GetDirectories(root).Where(p => !Path.GetFileName(p).StartsWith("_"))
                .FirstOrDefault(p => Directory.Exists(Path.Combine(p, session))) is string hit ? Path.GetFileName(hit) : null;

        static string TreeSnapshot(string root) => string.Join("\n",
            Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(p => p.Substring(root.Length) + "|" + (Directory.Exists(p) ? "D" : new FileInfo(p).Length.ToString()))
                .OrderBy(x => x));

        // ── cases ────────────────────────────────────────────────────────────

        static void Case_Happy()
        {
            Console.WriteLine("[A] happy path (stamped family, split family, inherit, mint, manifest-less)");
            string r = NewStore("A");
            AddSession(r, "world1",        "PIDAAA", 100);            // stamped base
            AddSession(r, "world1-auto",   "PIDAAA", 101);            // same family
            AddSession(r, "world1-recover", null, 102, manifest: false); // manifest-less → inherits family pid
            AddSession(r, "world2",        "PIDBBB", 200);
            AddSession(r, "world2-auto",   "PIDCCC", 201);            // CONTAMINATED variant → split, own pid wins
            AddSession(r, "legacy",        "",       300);            // pid-less family → one mint
            AddSession(r, "legacy-auto",   "",       301);
            var beforeMtime = Directory.GetLastWriteTimeUtc(Path.Combine(r, "world1"));
            Migrate(r);
            Check("A", File.Exists(Path.Combine(r, Marker)), "marker missing");
            Check("A", File.Exists(Path.Combine(r, UndoMap)), "undo map missing");
            Check("A", !File.Exists(Path.Combine(r, Journal)), "journal not finalized");
            Check("A", FlatLeftovers(r).Count == 0, "flat leftovers remain");
            Check("A", SessionsAtDepth2(r).Count == 7, $"expected 7 sessions, got {SessionsAtDepth2(r).Count}");
            Check("A", PidOf(r, "world1") == "PIDAAA" && PidOf(r, "world1-auto") == "PIDAAA", "world1 family not under PIDAAA");
            Check("A", PidOf(r, "world1-recover") == "PIDAAA", "manifest-less variant did not inherit family pid");
            Check("A", PidOf(r, "world2") == "PIDBBB" && PidOf(r, "world2-auto") == "PIDCCC", "split family not honored (own pid must win)");
            Check("A", PidOf(r, "legacy") == PidOf(r, "legacy-auto") && PidOf(r, "legacy").Length == 32, "legacy family not under ONE minted pid");
            Check("A", Directory.GetLastWriteTimeUtc(Path.Combine(r, "PIDAAA", "world1")) == beforeMtime, "folder timestamp not preserved");
        }

        static void Case_Idempotent()
        {
            Console.WriteLine("[B] second run is a no-op");
            string r = NewStore("B");
            AddSession(r, "w", "PIDAAA", 100);
            Migrate(r);
            string snap = TreeSnapshot(r);
            Resume(r);   // what a second launch runs when the marker exists
            Check("B", TreeSnapshot(r) == snap, "second run changed the store");
        }

        static void Case_StalePreAdoptionJournal()
        {
            Console.WriteLine("[C] crash BEFORE adoption: stale journal discarded, clean replan");
            string r = NewStore("C");
            AddSession(r, "w", "PIDAAA", 100);
            File.WriteAllText(Path.Combine(r, Journal), "{\"moves\":[{\"From\":\"w\",\"To\":\"WRONGPID/w\"}]}");
            Migrate(r);
            Check("C", PidOf(r, "w") == "PIDAAA", "stale journal was believed instead of replanned");
            Check("C", File.Exists(Path.Combine(r, UndoMap)), "undo map missing");
        }

        static void Case_ResumePartialRenames()
        {
            Console.WriteLine("[D] crash MID-renames: resume completes the remainder");
            string r = NewStore("D");
            AddSession(r, "w1", "PIDAAA", 100);
            AddSession(r, "w2", "PIDBBB", 200);
            // Simulate: marker + journal written, only w1's rename happened.
            File.WriteAllText(Path.Combine(r, Marker), "{\"format\":2}");
            File.WriteAllText(Path.Combine(r, Journal), Newtonsoft.Json.JsonConvert.SerializeObject(new
            { moves = new[] { new { From = "w1", To = "PIDAAA/w1" }, new { From = "w2", To = "PIDBBB/w2" } } }));
            Directory.CreateDirectory(Path.Combine(r, "PIDAAA"));
            Directory.Move(Path.Combine(r, "w1"), Path.Combine(r, "PIDAAA", "w1"));
            Resume(r);
            Check("D", PidOf(r, "w1") == "PIDAAA" && PidOf(r, "w2") == "PIDBBB", "resume did not complete renames");
            Check("D", !File.Exists(Path.Combine(r, Journal)) && File.Exists(Path.Combine(r, UndoMap)), "journal not finalized after resume");
        }

        static void Case_ResumeFinalizeOnly()
        {
            Console.WriteLine("[E] crash AFTER renames, before finalize: resume just finalizes");
            string r = NewStore("E");
            AddSession(r, "w", "PIDAAA", 100);
            File.WriteAllText(Path.Combine(r, Marker), "{\"format\":2}");
            File.WriteAllText(Path.Combine(r, Journal), Newtonsoft.Json.JsonConvert.SerializeObject(new
            { moves = new[] { new { From = "w", To = "PIDAAA/w" } } }));
            Directory.CreateDirectory(Path.Combine(r, "PIDAAA"));
            Directory.Move(Path.Combine(r, "w"), Path.Combine(r, "PIDAAA", "w"));
            Resume(r);
            Check("E", !File.Exists(Path.Combine(r, Journal)) && File.Exists(Path.Combine(r, UndoMap)), "finalize did not happen");
        }

        static void Case_AnomalyBothExist()
        {
            Console.WriteLine("[F] anomaly: BOTH source and destination exist — loud, nothing deleted");
            string r = NewStore("F");
            AddSession(r, "w", "PIDAAA", 100);
            File.WriteAllText(Path.Combine(r, Marker), "{\"format\":2}");
            File.WriteAllText(Path.Combine(r, Journal), Newtonsoft.Json.JsonConvert.SerializeObject(new
            { moves = new[] { new { From = "w", To = "PIDAAA/w" } } }));
            Directory.CreateDirectory(Path.Combine(r, "PIDAAA", "w"));
            File.WriteAllText(Path.Combine(r, "PIDAAA", "w", "existing.txt"), "already here");
            Resume(r);
            Check("F", Directory.Exists(Path.Combine(r, "w")), "source was deleted on anomaly");
            Check("F", File.Exists(Path.Combine(r, "PIDAAA", "w", "existing.txt")), "destination content lost");
            Check("F", File.Exists(Path.Combine(r, Journal)), "journal was finalized despite anomaly");
        }

        static void Case_AnomalyNeitherExist()
        {
            Console.WriteLine("[G] anomaly: NEITHER exists — loud, journal kept");
            string r = NewStore("G");
            File.WriteAllText(Path.Combine(r, Marker), "{\"format\":2}");
            File.WriteAllText(Path.Combine(r, Journal), Newtonsoft.Json.JsonConvert.SerializeObject(new
            { moves = new[] { new { From = "ghost", To = "PIDAAA/ghost" } } }));
            Resume(r);
            Check("G", File.Exists(Path.Combine(r, Journal)), "journal was finalized despite missing session");
        }

        static void Case_EmptyStore()
        {
            Console.WriteLine("[H] empty flat store: marker only");
            string r = NewStore("H");
            Migrate(r);
            Check("H", File.Exists(Path.Combine(r, Marker)), "marker missing");
            Check("H", !File.Exists(Path.Combine(r, UndoMap)), "undo map should not exist for empty store");
        }

        static void Case_LockedFileThenResume()
        {
            Console.WriteLine("[I] locked file mid-migration: fails loud, resume succeeds after release");
            string r = NewStore("I");
            AddSession(r, "w1", "PIDAAA", 100);
            AddSession(r, "w2", "PIDBBB", 200);
            string locked = Path.Combine(r, "w2", "guid-" + 0.ToString("D32"), "save.hsg");
            Exception thrown = null;
            using (var fs = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { Migrate(r); }
                catch (Exception ex) { thrown = ex.InnerException ?? ex; }
            }
            Check("I", thrown != null, "locked file did not surface as a failure");
            Check("I", File.Exists(Path.Combine(r, Marker)), "marker should exist (failure was post-adoption)");
            Check("I", File.Exists(Path.Combine(r, Journal)), "journal must survive the failure");
            Resume(r);   // lock released — resume must complete
            Check("I", PidOf(r, "w1") == "PIDAAA" && PidOf(r, "w2") == "PIDBBB", "resume after lock release failed");
            Check("I", File.Exists(Path.Combine(r, UndoMap)), "undo map missing after recovery");
        }

        static void Case_HexNamedSession()
        {
            Console.WriteLine("[J] flat session named like a pid (32-hex) still migrates");
            string r = NewStore("J");
            AddSession(r, "abcdefabcdefabcdefabcdefabcdefab", "PIDAAA", 100);
            Migrate(r);
            Check("J", PidOf(r, "abcdefabcdefabcdefabcdefabcdefab") == "PIDAAA", "hex-named session mishandled");
        }

        static void Case_RealFixtureFullScale()
        {
            Console.WriteLine("[K] full-scale real fixture (copy of the pre-migration rig store)");
            string src = @"C:\Users\allsc\AppData\LocalLow\Hovgaard Games\Big Ambitions\SaveGames\_BAMP_MP.v1hold\EA 0.11";
            if (!Directory.Exists(src)) { Console.WriteLine("  (fixture absent — skipped)"); return; }
            string r = NewStore("K");
            CopyTreePreserve(src, r);
            Migrate(r);
            var sess = SessionsAtDepth2(r);
            Check("K", sess.Count == 374, $"expected 374 sessions, got {sess.Count}");
            Check("K", FlatLeftovers(r).Count == 0, "flat leftovers remain");
            var undo = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(r, UndoMap)));
            Check("K", (int)undo["playthroughs"] == 117, $"expected 117 playthroughs, got {undo["playthroughs"]}");
        }

        static void CopyTreePreserve(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyTreePreserve(d, Path.Combine(dst, Path.GetFileName(d)));
            Directory.SetLastWriteTimeUtc(dst, Directory.GetLastWriteTimeUtc(src));
        }
    }
}
