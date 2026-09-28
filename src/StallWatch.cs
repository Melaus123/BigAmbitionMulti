using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace BigAmbitionsMP
{
    // PROBE-START: P-MIDNIGHT (2026-09-27, user-preapproved class, log-only). Field case: a client froze / died right
    // after '[Client] Received market snapshot.' with no '[Demand] host market applied', and the crashed session's
    // bamp-ring.log was lost. This file: (a) the mod's ring log persisted to disk every ~10 s when it changed, so a
    // hard crash keeps it (the next launch moves it aside as the previous session's and a crash report carries it);
    // (b) a cheap 'current step' the main thread sets at each sub-step of the market apply and of the game's day roll;
    // (c) a background watchdog: the main thread stamps a tick each frame; a stamp older than 5 s is logged ONCE per
    // stall with the step, the ring is flushed and the heartbeat marker's Phase names the step; the resume is logged.
    // THREADS: the main thread writes the stamp + step fields (plain volatile writes, no allocation); the watchdog
    // thread only reads them and writes files / log lines.
    internal static class StallWatch
    {
        private const int StallMs       = 5000;
        private const int RingFlushMs   = 10000;
        private const int MaxLineChars  = 4000;    // bound: 500 ring lines x 4000 chars = 2 MB worst case

        // ── main thread writes, watchdog reads ──
        private static volatile int     _stamp;
        private static volatile bool    _stamped;
        private static volatile string  _step = "idle";
        private static volatile string? _detail;     // a REFERENCE to an existing string (item name / row key) - never built here
        private static volatile int     _index = -1;
        private static volatile string? _detail2;
        private static volatile int     _sub = -1;
        private static int _mainThreadId = -1;

        /// <summary>Step changes since the last market-apply begin (main thread only).</summary>
        internal static int StepCount;

        // day-roll nesting (main thread only): GameManager.NewDay contains the helpers' RunDaily calls
        private static readonly string[] _stack = new string[8];
        private static int _depth;

        // ── watchdog state (watchdog thread only, read by the DEV lever) ──
        private static Thread? _thread;
        private static volatile bool _stalled;
        private static int _stallFromStamp;
        internal static volatile int Stalls, Resumes, RingWrites, StallFlushes;
        internal static volatile string LastStallText = "";
        private static object? _ringLastRef;

        // ── the per-frame stamp (MAIN THREAD, MPBugReport's canvas pre-block tick) ──

        public static void Frame()
        {
            _stamp = Environment.TickCount;
            if (_stamped) return;
            _stamped = true;
            try
            {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                _thread = new Thread(Watch) { IsBackground = true, Name = "BAMP StallWatch", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
                Plugin.Logger.LogInfo($"[Stall] watchdog started (stall = {StallMs / 1000} s without a frame; ring log kept at '{LivePath()}' every {RingFlushMs / 1000} s when it changed).");
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[Stall] watchdog start: {ex.GetType().Name}: {ex.Message}"); } catch { } }
        }

        // ── steps (MAIN THREAD; Step/Done ignore other threads) ──

        private static bool OnMain => _mainThreadId < 0 || Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public static void Step(string step)
        {
            if (!OnMain) return;
            _index = -1; _detail = null; _sub = -1; _detail2 = null;
            _step = step;
            StepCount++;
        }

        /// <summary>Per item: index + a reference to its key. No allocation.</summary>
        public static void Item(int index, string? key) { _sub = -1; _detail2 = null; _detail = key; _index = index; }

        /// <summary>Per row inside an item: index + a reference to its key. No allocation.</summary>
        public static void Row(int index, string? key) { _detail2 = key; _sub = index; }

        /// <summary>Back to the enclosing day-roll step, or idle.</summary>
        public static void Done()
        {
            if (!OnMain) return;
            _index = -1; _detail = null; _sub = -1; _detail2 = null;
            _step = _depth > 0 && _depth <= _stack.Length ? _stack[_depth - 1] : "idle";
        }

        internal static void Enter(string name)
        {
            if (_depth < _stack.Length) _stack[_depth] = name;
            _depth++;
            Step(name);
        }

        internal static void Exit()
        {
            if (_depth > 0) _depth--;
            Done();
        }

        // ── market apply begin / end (MAIN THREAD, GameStatePatcher.ApplyMarketSnapshot) ──

        private static int _marketT0;

        public static void MarketBegin(string? json)
        {
            try
            {
                int chars = json == null ? 0 : json.Length;
                StepCount = 0;
                _marketT0 = Environment.TickCount;
                Plugin.Logger.LogInfo($"[Midnight] market apply begin ({chars} bytes)");
                Step("market parse");
            }
            catch { }
        }

        public static void MarketEnd(int rows)
        {
            try
            {
                int ms = unchecked(Environment.TickCount - _marketT0);
                Plugin.Logger.LogInfo($"[Midnight] market apply end {ms} ms ({(rows >= 0 ? rows + " rows" : "no rows applied")}, {StepCount} steps)");
            }
            catch { }
            finally { Done(); }
        }

        /// <summary>Heartbeat Phase text: the phase plus the step when one is running.</summary>
        internal static string PhaseText(string phase)
        {
            try
            {
                string s = _step;
                if (s == "idle") return phase;
                return $"{phase}; step '{s}' ({Describe()})";
            }
            catch { return phase; }
        }

        private static string Describe()
        {
            var sb = new StringBuilder();
            int i = _index; string? d = _detail; int j = _sub; string? d2 = _detail2;
            if (i >= 0) sb.Append('#').Append(i).Append(" '").Append(d ?? "").Append('\'');
            else if (d != null) sb.Append(d);
            if (j >= 0) sb.Append(sb.Length > 0 ? ", " : "").Append("row #").Append(j).Append(" '").Append(d2 ?? "").Append('\'');
            return sb.Length > 0 ? sb.ToString() : "-";
        }

        // ── the watchdog (BACKGROUND THREAD) ──

        private static void Watch()
        {
            int lastFlush = Environment.TickCount;
            while (true)
            {
                try
                {
                    Thread.Sleep(250);
                    int now = Environment.TickCount;
                    int stamp = _stamp;
                    int age = unchecked(now - stamp);
                    if (!_stalled && age > StallMs)
                    {
                        _stalled = true;
                        _stallFromStamp = stamp;
                        Stalls++;
                        string step = _step, det = Describe();
                        LastStallText = $"{step} ({det})";
                        Plugin.Logger.LogWarning($"[Stall] main thread stuck {age / 1000.0:0.0} s at step '{step}' ({det})");
                        if (FlushRing(true)) StallFlushes++;
                        lastFlush = Environment.TickCount;
                        // M1: once the session closed cleanly the marker is never written again (log lines continue)
                        try { if (!MPBugReport.MarkerClosed) MPBugReport.WriteStallMarker($"STALLED at step '{step}' ({det})"); } catch { }
                    }
                    else if (_stalled && age < 1000)
                    {
                        _stalled = false;
                        Resumes++;
                        Plugin.Logger.LogInfo($"[Stall] resumed after {unchecked(stamp - _stallFromStamp) / 1000.0:0.0} s");
                    }
                    if (unchecked(now - lastFlush) >= RingFlushMs)
                    {
                        lastFlush = now;
                        FlushRing(false);
                    }
                }
                catch (ThreadAbortException) { return; }
                catch (Exception ex)
                {
                    try { Plugin.Logger.LogWarning($"[Stall] watchdog: {ex.GetType().Name}: {ex.Message}"); } catch { }
                    try { Thread.Sleep(5000); } catch { }
                }
            }
        }

        // ── the ring log on disk ──
        // MPLog (src/MPLog.cs, outside this probe's files) keeps the ring private; it is read here through its own lock.

        private static FieldInfo? _fLock, _fRing, _fHead, _fCount;
        private static bool _ringResolved, _ringMissingLogged;
        // M3: a failing ring write warns once and the live ring file is given up after 3 failures in a row
        private const int MaxFlushFailures = 3;
        private static int _flushFailures;
        private static bool _flushWarned, _flushOff;

        private static bool ResolveRing()
        {
            if (_ringResolved) return _fLock != null && _fRing != null && _fHead != null && _fCount != null;
            _ringResolved = true;
            const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Static;
            _fLock = typeof(MPLog).GetField("_lock", bf); _fRing = typeof(MPLog).GetField("_ring", bf);
            _fHead = typeof(MPLog).GetField("_head", bf); _fCount = typeof(MPLog).GetField("_count", bf);
            return _fLock != null && _fRing != null && _fHead != null && _fCount != null;
        }

        private static bool FlushRing(bool force)
        {
            if (_flushOff) return false;
            try
            {
                if (!ResolveRing())
                {
                    if (!_ringMissingLogged) { _ringMissingLogged = true; Plugin.Logger.LogWarning("[Stall] ring log fields not found - the live ring file is off."); }
                    return false;
                }
                var lk = _fLock!.GetValue(null);
                var ring = _fRing!.GetValue(null) as string[];
                if (lk == null || ring == null || ring.Length == 0) return false;
                // M2: only the line REFERENCES are copied inside MPLog's lock; the text is built outside it
                string?[] lines;
                lock (lk)
                {
                    int head = (int)_fHead!.GetValue(null);
                    int count = (int)_fCount!.GetValue(null);
                    if (count <= 0) return false;
                    object last = ring[(head - 1 + ring.Length) % ring.Length];
                    if (!force && ReferenceEquals(last, _ringLastRef)) return false;
                    _ringLastRef = last;
                    if (count > ring.Length) count = ring.Length;
                    int start = count < ring.Length ? 0 : head;
                    lines = new string?[count];
                    for (int i = 0; i < count; i++) lines[i] = ring[(start + i) % ring.Length];
                }
                var sb = new StringBuilder(64 * 1024);
                sb.Append("# BAMP live ring - written ").Append(DateTime.Now.ToString("O"))
                  .Append(" role=").Append(MPServer.IsRunning ? "host" : (MPClient.IsConnected ? "client" : "offline"))
                  .Append(" player='").Append(MPConfig.PlayerId).Append("' session=").Append(MPLog.SessionId)
                  .Append(force ? " (stall flush)" : "").Append("\r\n");
                foreach (var l in lines)
                {
                    if (l == null) continue;
                    if (l.Length > MaxLineChars) sb.Append(l, 0, MaxLineChars).Append(" ...[cut]"); else sb.Append(l);
                    sb.Append("\r\n");
                }
                string path = LivePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                try { if (File.Exists(path)) File.Delete(path); File.Move(tmp, path); }
                catch { File.Copy(tmp, path, true); try { File.Delete(tmp); } catch { } }
                RingWrites++;
                _flushFailures = 0;
                return true;
            }
            catch (Exception ex)
            {
                _flushFailures++;
                if (!_flushWarned)
                {
                    _flushWarned = true;
                    try { Plugin.Logger.LogWarning($"[Stall] ring flush: {ex.GetType().Name}: {ex.Message} (warned once; the live ring file stops after {MaxFlushFailures} failures in a row)"); } catch { }
                }
                if (_flushFailures >= MaxFlushFailures)
                {
                    _flushOff = true;
                    try { Plugin.Logger.LogWarning($"[Stall] ring flush failed {_flushFailures} times in a row - the live ring file is off for this session."); } catch { }
                }
                return false;
            }
        }

        private static string LogDir()
        {
            try
            {
                string root = MPConfig.DataRootPath;
                if (!string.IsNullOrEmpty(root) && root != ".") return Path.Combine(root, "logs");
            }
            catch { }
            return Path.Combine(Path.GetTempPath(), "BigAmbitionsMP-logs");
        }

        /// <summary>Per install (the rig's instances share the data folder).</summary>
        private static string InstallKey()
        {
            try
            {
                var name = Path.GetFileName((MPConfig.GameRootPath ?? "").TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) return "default";
                foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                return name.Replace(' ', '_');
            }
            catch { return "default"; }
        }

        internal static string LivePath() => Path.Combine(LogDir(), $"bamp-ring-live-{InstallKey()}.log");
        internal static string PrevPath() => Path.Combine(LogDir(), $"bamp-ring-live-{InstallKey()}-prev.log");

        /// <summary>Session start (before the first frame): the previous session's live ring becomes '-prev'.</summary>
        internal static void RotatePrevious()
        {
            try
            {
                string live = LivePath(), prev = PrevPath();
                if (!File.Exists(live))
                {
                    // M4: no live ring from the previous session - an older '-prev' is stale and must not be attached as its ring
                    if (File.Exists(prev)) { File.Delete(prev); Plugin.Logger.LogInfo($"[Stall] no live ring from the previous session - the stale '{prev}' was removed."); }
                    return;
                }
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(live, prev);
                Plugin.Logger.LogInfo($"[Stall] previous session's live ring kept as '{prev}'.");
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[Stall] rotate: {ex.GetType().Name}: {ex.Message}"); } catch { } }
        }

#if BAMP_DEV
        /// <summary>DEV lever 'stalltest &lt;seconds&gt;' (MAIN THREAD): block the main thread; 'stalltest' alone reads the state.</summary>
        internal static string DevLever(string arg)
        {
            arg = (arg ?? "").Trim();
            // M1 (DEV): 'stalltest close' runs the clean-shutdown marker delete; 'stalltest marker' reads whether the marker exists
            if (arg == "close") { MPBugReport.MarkCleanShutdown(); return $"OK stalltest close marker={(MPBugReport.MarkerFileExists() ? "present" : "absent")} closed={MPBugReport.MarkerClosed}"; }
            if (arg == "marker") return $"OK stalltest marker={(MPBugReport.MarkerFileExists() ? "present" : "absent")} closed={MPBugReport.MarkerClosed} stalls={Stalls}";
            if (arg.Length > 0)
            {
                if (!int.TryParse(arg, out var s) || s < 1 || s > 30) return "ERR usage: stalltest <1-30 seconds>";
                Step("dev stalltest");
                Item(s, "stalltest");
                try { Thread.Sleep(s * 1000); } finally { Done(); }
                return $"OK stalltest blocked the main thread {s} s";
            }
            long bytes = -1; double age = -1;
            try { var fi = new FileInfo(LivePath()); if (fi.Exists) { bytes = fi.Length; age = (DateTime.Now - fi.LastWriteTime).TotalSeconds; } } catch { }
            return $"OK stallwatch thread={(_thread != null && _thread.IsAlive ? "alive" : "dead")} stalls={Stalls} resumed={Resumes} stallflush={StallFlushes}"
                 + $" ringwrites={RingWrites} ringbytes={bytes} ringage={age:0.0}s last='{LastStallText}' step='{_step}'";
        }
#endif
    }

    /// <summary>P-MIDNIGHT (b): the game's day roll as steps - GameManager.NewDay and each daily pass it runs (GameManager.cs:638-669)
    /// plus the midnight autosave check. First prefix / last finalizer, so the mod's own patches on these run INSIDE the step.</summary>
    [HarmonyPatch]
    public static class Patch_StallWatch_DayRollSteps
    {
        private static readonly Dictionary<MethodBase, string> _names = new Dictionary<MethodBase, string>();
        private static MethodBase? _newDay;
        private static int _t0;

        static IEnumerable<MethodBase> TargetMethods()
        {
            var list = new List<MethodBase>();
            foreach (var spec in new[]
            {
                "GameManager:NewDay", "GameManager:RunMidNightAutoSave",
                "Helpers.RealEstateHelper:RunDaily", "Helpers.EmployeeHelper:PayDailyWages", "Helpers.EmployeeHelper:WorkDaily",
                "Helpers.ParkingSimulator:RunDaily", "Helpers.BusinessHelper:RunDaily", "Helpers.CompetitionHelper:RunDaily",
                "Helpers.ProductMarketHelper:RunDaily", "AdManager:RunDaily", "Helpers.TaxHelper:RunDaily",
                "UI.DailySummary.DailySummary:Run", "Helpers.InvestmentFundHelper:RunDaily", "Helpers.EmployeeHelper:RunDaily",
                "BigAmbitions.Rivals.RivalsHelper:RunDaily",
            })
            {
                try
                {
                    int c = spec.IndexOf(':');
                    var t = AccessTools.TypeByName(spec.Substring(0, c));
                    var m = t == null ? null : AccessTools.Method(t, spec.Substring(c + 1));
                    if (m == null) { Plugin.Logger.LogWarning($"[Stall] day-roll step target '{spec}' not found."); continue; }
                    if (_names.ContainsKey(m)) continue;
                    _names[m] = spec.Substring(spec.LastIndexOf('.', c) + 1).Replace(':', '.');
                    if (spec == "GameManager:NewDay") _newDay = m;
                    list.Add(m);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Stall] day-roll step target '{spec}': {ex.GetType().Name}: {ex.Message}"); }
            }
            return list;
        }

        [HarmonyPriority(Priority.First)]
        static void Prefix(MethodBase __originalMethod)
        {
            try
            {
                if (!_names.TryGetValue(__originalMethod, out var n)) return;
                if (ReferenceEquals(__originalMethod, _newDay))
                {
                    _t0 = Environment.TickCount;
                    int day = -1; try { day = SaveGameManager.Current != null ? SaveGameManager.Current.Day : -1; } catch { }
                    Plugin.Logger.LogInfo($"[Midnight] day roll begin (day {day} -> {day + 1})");
                }
                StallWatch.Enter(n);
            }
            catch { }
        }

        [HarmonyPriority(Priority.Last)]
        static void Finalizer(MethodBase __originalMethod)
        {
            try
            {
                if (!_names.ContainsKey(__originalMethod)) return;
                StallWatch.Exit();
                if (ReferenceEquals(__originalMethod, _newDay))
                    Plugin.Logger.LogInfo($"[Midnight] day roll end {unchecked(Environment.TickCount - _t0)} ms");
            }
            catch { }
        }
    }
    // PROBE-END: P-MIDNIGHT
}
