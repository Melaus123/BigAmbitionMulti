using System;
using System.Diagnostics;
using System.IO;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Lightweight load/sync timeline profiler.  Measures WHERE time goes during the
    /// connect → load → go-live sequence so we can frontload the heavy work into the
    /// wait screen instead of guessing.  Thread-safe (a shared Stopwatch + DateTime),
    /// so it works on the network poll thread and the Unity main thread alike.
    ///
    /// Output: BepInEx log lines tagged "[LoadProf]" + a per-process file
    /// C:\dumps\loadprof.&lt;pid&gt;.txt (host and client get separate files; align by the
    /// wall-clock timestamp since both run on the same machine in tests).
    ///
    /// Read it by grepping "[LoadProf]" in each instance's log, or diffing the two
    /// loadprof files.  Each line: "&lt;wall&gt;  [&lt;elapsed&gt; ms][t&lt;thread&gt;] &lt;label&gt;".
    /// Durations are logged as "&lt;label&gt; took &lt;n&gt; ms" via Span().
    /// </summary>
    public static class MPLoadProfiler
    {
        /// <summary>Master on/off — flip to false to silence in normal play.</summary>
        public static bool Enabled = false;   // load-timing investigation done — re-enable when profiling

        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        private static readonly string    _file = $@"C:\dumps\loadprof.{Environment.ProcessId}.txt";
        private static readonly object    _lock = new();
        private static StreamWriter?      _w;

        /// <summary>Milliseconds since the profiler started (process load).  Safe on any thread.</summary>
        public static long NowMs => _sw.ElapsedMilliseconds;

        /// <summary>Log a point-in-time event on the load timeline.</summary>
        public static void Mark(string label)
        {
            if (!Enabled) return;
            Emit($"{label}");
        }

        /// <summary>Log a completed duration: call NowMs before the work, pass it here after.</summary>
        public static void Span(string label, long startMs)
        {
            if (!Enabled) return;
            Emit($"{label}  took {_sw.ElapsedMilliseconds - startMs} ms");
        }

        private static void Emit(string body)
        {
            string line = $"[{_sw.ElapsedMilliseconds,8} ms][t{Environment.CurrentManagedThreadId}] {body}";
            try { Plugin.Logger.LogInfo("[LoadProf] " + line); } catch { }
            try
            {
                lock (_lock)
                {
                    if (_w == null)
                    {
                        try { Directory.CreateDirectory(Path.GetDirectoryName(_file)!); } catch { }
                        _w = new StreamWriter(new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                        _w.WriteLine($"==== loadprof session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ====");
                    }
                    _w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
                }
            }
            catch { }
        }
    }
}
