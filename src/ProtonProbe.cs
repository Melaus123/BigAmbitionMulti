// PROBE-START: P-T71-WINEDETOUR
// Registered field probe (row T-71). It SHIPS in release builds on purpose (not behind BAMP_DEV):
// Linux players (Steam Proton = Wine) must run it. Log lines only, prefix [PROBE T71].
// Under plain Windows it does not scan at start-up: it only arms the first-"+ 0x00000>"-fault hook
// (row T-75: a Windows preview-build host showed the same entry fault), so a Windows bundle with that
// fault carries the scan too. The DEV lever `winescan` runs the scan regardless.
// 2026-11-17 (rows T-74/T-75): the Wine fault was traced to MonoMod.Core 1.3.3 (Harmony 2.4.2) writing
// Rel32Ind64 cells more than 2 GB from methods that sit below 2 GB (upstream MonoMod#308); the mod now
// bundles Harmony 2.4.3 (MonoMod.Core 1.3.6) - unless another mod's older Harmony loads first, in which case the
// player runs that copy without the fix (Plugin's 'Harmony provenance' line names it). The probe stays to show that the
// next bundles are clean.
//
// What it reads: for every method Harmony has patched, the ORIGINAL's native entry, where
// MonoMod.Core (embedded in 0Harmony; first verified in 2.4.2) wrote the detour jump. Verified in the decompiled
// 0Harmony 2.4.2: PlatformTripleDetourFactory.ManagedDetourBox.CreateDetour takes
// from = Triple.GetNativeMethodBody(Source); MonoRuntime does not request body-thunk walking, so
// that is GetNativeMethodBodyDirect -> MonoRuntime.GetMethodEntryPoint -> method.MethodHandle
// .GetFunctionPointer(). The jump kinds x86_64Arch writes: Rel32 `E9 rel32` (5 bytes), Rel32Ind64
// `FF 25 disp32` with the 8-byte target in a separately allocated PAGE_READWRITE cell within +-2 GB,
// Abs64 `FF 25 00000000` + inline imm64 (14 bytes). `48 B8 imm64 FF E0` is decoded too, though
// MonoMod.Core (1.3.3 in Harmony 2.4.2, 1.3.6 in 2.4.3) does not write it as a detour. Every page is VirtualQuery'd before any read.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace BigAmbitionsMP
{
    internal static class ProtonProbe
    {
        private const string P = "[PROBE T71] ";
        private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_FREE = 0x10000;
        private const uint PAGE_GUARD = 0x100;
        private const int MaxAnomalyLines = 40, MaxHealthyLines = 5, MaxBlocksListed = 64;

        private static bool? _isWine;
        private static string _wineVersion = "";
        private static bool _hooked, _fired;

        [StructLayout(LayoutKind.Sequential)]
        private struct MBI   // MEMORY_BASIC_INFORMATION, x64 layout (48 bytes)
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public ushort Pad1;
            public UIntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Pad2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MBI lpBuffer, UIntPtr dwLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr WineGetVersionFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WineGetHostVersionFn(out IntPtr sysname, out IntPtr release);

        private sealed class ScanResult
        {
            public int Scanned, EntryNonExec, CellUnreadable, TargetBad, NoJump, Unreadable, Errors, Anomalies, In7b;
            public readonly SortedSet<long> Blocks = new SortedSet<long>();
            public readonly SortedDictionary<string, int> Kinds = new SortedDictionary<string, int>();
        }

        // ---------------------------------------------------------------- entry points

        /// <summary>Called once from Plugin right after the "[Plugin] Patch summary" line. Never throws.</summary>
        internal static void AfterPatching()
        {
            try
            {
                if (!IsWine()) { Hook(); SafeLog("not under Wine; armed for the first +0x00000 fault only."); return; }
                LogEnvironment();
                LogVersions();
                RunScan("after patching", 0);
                Hook();
            }
            catch (Exception ex) { SafeLog("AfterPatching failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>DEV lever `winescan` (TestDrive): the same scan regardless of Wine.</summary>
        internal static string DevScan()
        {
            try
            {
                bool wine = IsWine();
                if (wine) LogEnvironment();
                LogVersions();
                var r = RunScan("dev winescan", 0);
                return $"OK winescan wine={(wine ? "true" : "false")} scanned={r.Scanned} anomalies={r.Anomalies} blocks={r.Blocks.Count} kinds={KindsText(r)}";
            }
            catch (Exception ex) { return "ERR winescan " + ex.GetType().Name + ": " + ex.Message; }
        }

        // ---------------------------------------------------------------- Wine / environment

        internal static bool IsWine()
        {
            if (_isWine.HasValue) return _isWine.Value;
            bool wine = false;
            try
            {
                IntPtr nt = GetModuleHandleW("ntdll.dll");
                IntPtr f = nt == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(nt, "wine_get_version");
                if (f != IntPtr.Zero)
                {
                    wine = true;
                    try
                    {
                        var fn = (WineGetVersionFn)Marshal.GetDelegateForFunctionPointer(f, typeof(WineGetVersionFn));
                        IntPtr s = fn();
                        _wineVersion = s == IntPtr.Zero ? "(null)" : (Marshal.PtrToStringAnsi(s) ?? "?");
                    }
                    catch (Exception ex) { _wineVersion = "(call failed: " + ex.GetType().Name + ")"; }
                }
            }
            catch { wine = false; }
            _isWine = wine;
            return wine;
        }

        private static void LogEnvironment()
        {
            var sb = new StringBuilder("Wine detected: wine_get_version=").Append(_wineVersion);
            try
            {
                IntPtr nt = GetModuleHandleW("ntdll.dll");
                IntPtr f = nt == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(nt, "wine_get_host_version");
                if (f != IntPtr.Zero)
                {
                    var fn = (WineGetHostVersionFn)Marshal.GetDelegateForFunctionPointer(f, typeof(WineGetHostVersionFn));
                    fn(out IntPtr sys, out IntPtr rel);
                    sb.Append(" host=").Append(sys == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(sys))
                      .Append(' ').Append(rel == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(rel));
                }
            }
            catch (Exception ex) { sb.Append(" host=(failed ").Append(ex.GetType().Name).Append(')'); }

            // Names only (never values), except the Proton tool FOLDER names from STEAM_COMPAT_TOOL_PATHS.
            try
            {
                var names = new List<string>();
                foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
                {
                    string n = de.Key?.ToString() ?? "";
                    if (n.StartsWith("PROTON_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("WINE", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("STEAM_COMPAT_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("DXVK_", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("VKD3D_", StringComparison.OrdinalIgnoreCase) || n.Equals("SteamDeck", StringComparison.OrdinalIgnoreCase)
                        || n.Equals("SteamOS", StringComparison.OrdinalIgnoreCase))
                        names.Add(n);
                }
                names.Sort(StringComparer.Ordinal);
                sb.Append(" envNames=[").Append(string.Join(",", names)).Append(']');

                string? tools = Environment.GetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS");
                if (!string.IsNullOrEmpty(tools))
                {
                    var folders = new List<string>();
                    foreach (var part in tools!.Split(new[] { ':', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = part.TrimEnd('/', '\\');
                        int i = Math.Max(t.LastIndexOf('/'), t.LastIndexOf('\\'));
                        string leaf = i >= 0 ? t.Substring(i + 1) : t;
                        if (leaf.Length > 0) folders.Add(leaf);
                    }
                    sb.Append(" toolFolders=[").Append(string.Join(" | ", folders)).Append(']');
                }
            }
            catch (Exception ex) { sb.Append(" env=(failed ").Append(ex.GetType().Name).Append(')'); }
            SafeLog(sb.ToString());
        }

        private static void LogVersions()
        {
            var sb = new StringBuilder();
            try
            {
                var h = typeof(Harmony).Assembly;
                string info = h.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                               .OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";
                var tripleType = h.GetType("MonoMod.Core.Platforms.PlatformTriple", false);
                sb.Append("Harmony v").Append(h.GetName().Version).Append(" (").Append(info).Append(')')
                  .Append(" MonoMod.Core ").Append(tripleType != null ? "embedded in 0Harmony" : "NOT embedded in 0Harmony");

                var mm = new List<string>();
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var an = a.GetName();
                    if (an.Name != null && an.Name.StartsWith("MonoMod", StringComparison.OrdinalIgnoreCase)) mm.Add(an.Name + " v" + an.Version);
                }
                sb.Append("; separate MonoMod assemblies loaded: ").Append(mm.Count == 0 ? "none" : string.Join(", ", mm));

                try
                {
                    var os = h.GetType("MonoMod.Utils.PlatformDetection", false)?.GetProperty("OS", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null, null);
                    sb.Append("; MonoMod sees OS=").Append(os?.ToString() ?? "?");
                    var cur = tripleType?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null, null);
                    if (cur != null)
                    {
                        foreach (var pn in new[] { "System", "Architecture", "Runtime" })
                            sb.Append(' ').Append(pn).Append('=').Append(tripleType!.GetProperty(pn)?.GetValue(cur, null)?.GetType().Name ?? "?");
                    }
                }
                catch (Exception ex) { sb.Append("; triple=(failed ").Append(ex.GetType().Name).Append(')'); }
            }
            catch (Exception ex) { sb.Append("versions failed: ").Append(ex.GetType().Name); }
            SafeLog(sb.ToString());
        }

        // ---------------------------------------------------------------- the scan

        private static ScanResult RunScan(string reason, long matchAddr)
        {
            var r = new ScanResult();
            var anomalous = new List<string>();
            var healthy = new List<string>();
            List<MethodBase> methods;
            try { methods = Harmony.GetAllPatchedMethods().ToList(); }
            catch (Exception ex) { SafeLog("scan (" + reason + "): GetAllPatchedMethods failed: " + ex.GetType().Name + ": " + ex.Message); return r; }

            foreach (var m in methods)
            {
                if (m == null) continue;
                string line;
                bool bad;
                try { bad = ScanOne(m, r, matchAddr, out line); }
                catch (Exception ex)
                {
                    bad = true; r.Errors++; r.Anomalies++;
                    line = SafeName(m) + " scan error " + ex.GetType().Name + ": " + ex.Message;
                }
                if (bad) { if (anomalous.Count < MaxAnomalyLines) anomalous.Add(line); }
                else if (healthy.Count < MaxHealthyLines) healthy.Add(line);
            }

            var blocks = r.Blocks.Take(MaxBlocksListed).Select(b => "0x" + b.ToString("x"));
            string more = r.Blocks.Count > MaxBlocksListed ? " (+" + (r.Blocks.Count - MaxBlocksListed) + " more)" : "";
            SafeLog($"scan ({reason}): scanned={r.Scanned} anomalies={r.Anomalies} entryPageNonExec={r.EntryNonExec} cellUnreadable={r.CellUnreadable} " +
                    $"targetPageNonExecOrUncommitted={r.TargetBad} noKnownJump={r.NoJump} entryUnreadable={r.Unreadable} errors={r.Errors} " +
                    $"entriesIn0x7b000000-0x7bffffff={r.In7b} kinds={KindsText(r)} blocks64K={r.Blocks.Count} [{string.Join(",", blocks)}]{more}");
            foreach (var l in anomalous) SafeLog("ANOMALY " + l);
            if (r.Anomalies > anomalous.Count) SafeLog("(" + (r.Anomalies - anomalous.Count) + " more anomalous method(s) not listed)");
            foreach (var l in healthy) SafeLog("healthy " + l);
            return r;
        }

        private static bool ScanOne(MethodBase m, ScanResult r, long matchAddr, out string line)
        {
            r.Scanned++;
            IntPtr entry = m.MethodHandle.GetFunctionPointer();   // the original's JIT entry = MonoMod's detour `from` (see header)
            long e = entry.ToInt64();
            r.Blocks.Add(e & ~0xFFFFL);
            if (e >= 0x7b000000L && e <= 0x7bffffffL) r.In7b++;

            var sb = new StringBuilder(SafeName(m)).Append(" entry=0x").Append(e.ToString("x"));
            if (matchAddr != 0 && matchAddr == e) sb.Append(" [= the faulting frame's address]");
            bool bad = false;

            if (!Query(entry, out MBI em)) { r.Unreadable++; r.Anomalies++; Count(r, "unread"); sb.Append(" entryPage=QUERYFAIL"); line = sb.ToString(); return true; }
            sb.Append(" entryPage=").Append(Desc(em));
            if (!IsExec(em)) { r.EntryNonExec++; bad = true; }

            if (!CanRead(entry, 16)) { r.Unreadable++; r.Anomalies++; Count(r, "unread"); sb.Append(" bytes=UNREADABLE"); line = sb.ToString(); return true; }
            var b = new byte[16];
            Marshal.Copy(entry, b, 0, 16);
            sb.Append(" bytes=").Append(Hex(b));

            string kind;
            long target = 0;
            bool haveTarget = false;
            if (b[0] == 0xE9)
            {
                kind = "rel32";
                target = e + 5 + BitConverter.ToInt32(b, 1);
                haveTarget = true;
            }
            else if (b[0] == 0xFF && b[1] == 0x25)
            {
                int disp = BitConverter.ToInt32(b, 2);
                long cell = e + 6 + disp;
                kind = disp == 0 ? "abs64" : "rel32ind64";
                sb.Append(" cell=0x").Append(cell.ToString("x"));
                sb.Append(" cellPage=").Append(Query(new IntPtr(cell), out MBI cm) ? Desc(cm) : "QUERYFAIL");
                if (CanRead(new IntPtr(cell), 8)) { target = Marshal.ReadInt64(new IntPtr(cell)); haveTarget = true; }
                else { r.CellUnreadable++; bad = true; sb.Append(" cellRead=UNREADABLE"); }
            }
            else if (b[0] == 0x48 && b[1] == 0xB8 && b[10] == 0xFF && b[11] == 0xE0)
            {
                kind = "movraxjmp";
                target = BitConverter.ToInt64(b, 2);
                haveTarget = true;
            }
            else { kind = "nojump"; r.NoJump++; bad = true; }
            Count(r, kind);
            sb.Append(" kind=").Append(kind);

            if (haveTarget)
            {
                sb.Append(" target=0x").Append(target.ToString("x"));
                if (Query(new IntPtr(target), out MBI tm))
                {
                    sb.Append(" targetPage=").Append(Desc(tm));
                    if (!IsExec(tm)) { r.TargetBad++; bad = true; }
                }
                else { sb.Append(" targetPage=QUERYFAIL"); r.TargetBad++; bad = true; }
            }
            if (bad) r.Anomalies++;
            line = sb.ToString();
            return bad;
        }

        // ---------------------------------------------------------------- first "+ 0x00000>" exception

        private static void Hook()
        {
            if (_hooked || _fired) return;
            try { UnityEngine.Application.logMessageReceived += OnLog; _hooked = true; }
            catch (Exception ex) { SafeLog("hook failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void Unhook()
        {
            try { if (_hooked) UnityEngine.Application.logMessageReceived -= OnLog; } catch { }
            _hooked = false;
        }

        private static readonly Regex NativeAddr = new Regex(@"<0x([0-9a-fA-F]+) \+ 0x00000>", RegexOptions.CultureInvariant);

        private static void OnLog(string condition, string stackTrace, UnityEngine.LogType type)
        {
            try
            {
                if (_fired) return;
                bool inCond = condition != null && condition.IndexOf("+ 0x00000>", StringComparison.Ordinal) >= 0;
                bool inStack = stackTrace != null && stackTrace.IndexOf("+ 0x00000>", StringComparison.Ordinal) >= 0;
                if (!inCond && !inStack) return;
                string? top = FirstFrame(condition) ?? FirstFrame(stackTrace);
                if (top == null || top.IndexOf("+ 0x00000>", StringComparison.Ordinal) < 0) return;

                _fired = true;
                Unhook();   // before any logging of ours: our own lines must not re-enter

                if (top.Length > 300) top = top.Substring(0, 300);
                long addr = 0;
                var mt = NativeAddr.Match(top);
                if (mt.Success) { try { addr = Convert.ToInt64(mt.Groups[1].Value, 16); } catch { addr = 0; } }

                var sb = new StringBuilder("first '+ 0x00000>' top frame (log type ").Append(type).Append("): ").Append(top);
                if (addr != 0)
                {
                    sb.Append(" | addr=0x").Append(addr.ToString("x"));
                    var ap = new IntPtr(addr);
                    sb.Append(" page=").Append(Query(ap, out MBI am) ? Desc(am) : "QUERYFAIL");
                    if (CanRead(ap, 16)) { var b = new byte[16]; Marshal.Copy(ap, b, 0, 16); sb.Append(" bytes=").Append(Hex(b)); }
                    else sb.Append(" bytes=UNREADABLE");
                }
                SafeLog(sb.ToString());
                RunScan("first +0x00000 exception", addr);
            }
            catch { /* never throw from a log handler */ }
        }

        private static string? FirstFrame(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = text!;
            foreach (var raw in text.Split('\n'))
            {
                string t = raw.Trim();
                if (t.StartsWith("at ", StringComparison.Ordinal)) return t;
            }
            // one-line form: "... at Ns.Type.Method () <0x... + 0x00000> ..."
            int i = text.IndexOf(" at ", StringComparison.Ordinal);
            if (i < 0) return null;
            int end = text.IndexOf('\n', i);
            string rest = end < 0 ? text.Substring(i + 1) : text.Substring(i + 1, end - i - 1);
            int gt = rest.IndexOf('>');
            return (gt >= 0 ? rest.Substring(0, gt + 1) : rest).Trim();
        }

        // ---------------------------------------------------------------- memory helpers (every read is page-checked first)

        private static bool Query(IntPtr addr, out MBI mi)
        {
            mi = default;
            try
            {
                if (addr.ToInt64() <= 0) return false;
                return VirtualQuery(addr, out mi, new UIntPtr((uint)Marshal.SizeOf(typeof(MBI)))) != UIntPtr.Zero;
            }
            catch { return false; }
        }

        private static bool IsReadable(MBI mi)
        {
            if (mi.State != MEM_COMMIT || (mi.Protect & PAGE_GUARD) != 0) return false;
            uint p = mi.Protect & 0xFF;
            return p == 0x02 || p == 0x04 || p == 0x08 || p == 0x20 || p == 0x40 || p == 0x80;
        }

        private static bool IsExec(MBI mi) =>
            mi.State == MEM_COMMIT && (mi.Protect & PAGE_GUARD) == 0 && (mi.Protect & 0xF0) != 0;

        /// <summary>True only when every byte of [addr, addr+len) lies in committed, readable, non-guard pages.</summary>
        private static bool CanRead(IntPtr addr, int len)
        {
            try
            {
                long a = addr.ToInt64(), end = a + len;
                if (a <= 0) return false;
                while (a < end)
                {
                    if (!Query(new IntPtr(a), out MBI mi) || !IsReadable(mi)) return false;
                    long regEnd = mi.BaseAddress.ToInt64() + (long)mi.RegionSize.ToUInt64();
                    if (regEnd <= a) return false;
                    a = regEnd;
                }
                return true;
            }
            catch { return false; }
        }

        private static string Desc(MBI mi)
        {
            string st = mi.State == MEM_COMMIT ? "COMMIT" : mi.State == MEM_RESERVE ? "RESERVE" : mi.State == MEM_FREE ? "FREE" : "0x" + mi.State.ToString("x");
            return st + ",prot=0x" + mi.Protect.ToString("x") + ",allocBase=0x" + mi.AllocationBase.ToInt64().ToString("x")
                 + ",allocProt=0x" + mi.AllocationProtect.ToString("x") + ",region=0x" + mi.BaseAddress.ToInt64().ToString("x")
                 + "+0x" + mi.RegionSize.ToUInt64().ToString("x");
        }

        // ---------------------------------------------------------------- small helpers

        private static void Count(ScanResult r, string kind) { r.Kinds.TryGetValue(kind, out int n); r.Kinds[kind] = n + 1; }

        private static string KindsText(ScanResult r) => r.Kinds.Count == 0 ? "none" : string.Join(",", r.Kinds.Select(k => k.Key + ":" + k.Value));

        private static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", " ");

        private static string SafeName(MethodBase m)
        {
            try { return (m.DeclaringType?.FullName ?? "?") + "::" + m.Name; } catch { return "?"; }
        }

        private static void SafeLog(string s)
        {
            try { Plugin.Logger.LogInfo(P + s); } catch { }
        }
    }
}
// PROBE-END: P-T71-WINEDETOUR
