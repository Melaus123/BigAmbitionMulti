using System;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// T0 of the 2026-08 throughput effort (user-approved): per-message-type byte counters, both
    /// directions, printed every 30 s. The audit's central observability finding: SizeWatch only fires
    /// on a SINGLE message over 300 KB, so a 22 KB snapshot sent ten times a second — the largest
    /// stream in the mod — was invisible in every field log. This makes rate × size visible, so each
    /// throughput fix (T1 compression, T2 traffic, …) gets a real before/after from an ordinary session.
    ///
    /// OUT is counted at the four concrete transport Send(byte[]) funnels (host links + client
    /// transports, both UDP and Steam), parsing the type from the envelope head ({"t":NN,...}) — one
    /// count per actual wire send, so a broadcast to 3 peers counts 3×. Bytes Steam later retries are
    /// not re-counted, and the paced StoreMirror lane is counted at hand-off, not per chunk. IN is
    /// counted at the two OnReceive seams with the exact received length. Unparseable heads land in
    /// bucket 0 so a future framing change (T1's compression marker) shows up instead of vanishing.
    ///
    /// Log only (ruling 17 does not apply to logs); ~zero cost: two Interlocked ops per message.
    /// </summary>
    public static class MPNetStats
    {
        private const int   Slots           = 512;   // MessageType values are well below this
        private const float ReportSeconds   = 30f;
        private const int   TopRows         = 10;
        private const long  QuietThreshold  = 1024;  // skip the report when a direction moved under 1 KB

        private static readonly long[] _outBytes = new long[Slots];
        private static readonly int[]  _outMsgs  = new int[Slots];
        private static readonly long[] _inBytes  = new long[Slots];
        private static readonly int[]  _inMsgs   = new int[Slots];
        private static float _nextReport;

        public static void Reset()
        {
            for (int i = 0; i < Slots; i++)
            {
                System.Threading.Interlocked.Exchange(ref _outBytes[i], 0); _outMsgs[i] = 0;
                System.Threading.Interlocked.Exchange(ref _inBytes[i], 0);  _inMsgs[i]  = 0;
            }
            _nextReport = 0f;
        }

        /// <summary>Transport funnels call this with the exact bytes handed to the wire. Thread-safe —
        /// the Steam pump thread sends too.</summary>
        public static void NoteOut(byte[] data)
        {
            if (data == null) return;
            int t = ParseType(data);
            System.Threading.Interlocked.Add(ref _outBytes[t], data.Length);
            System.Threading.Interlocked.Increment(ref _outMsgs[t]);
        }

        /// <summary>Review M7: the Steam paced lane sends FRAGMENT frames whose head the peek cannot
        /// read — the caller names the type it is carrying (today: only StoreMirror rides it).</summary>
        public static void NoteOutAs(int type, int length)
        {
            int t = type >= 0 && type < Slots ? type : 0;
            System.Threading.Interlocked.Add(ref _outBytes[t], length);
            System.Threading.Interlocked.Increment(ref _outMsgs[t]);
        }

        public static void NoteIn(int type, int length)
        {
            int t = type >= 0 && type < Slots ? type : 0;
            System.Threading.Interlocked.Add(ref _inBytes[t], length);
            System.Threading.Interlocked.Increment(ref _inMsgs[t]);
        }

        /// <summary>T3 (Steam lanes) routes bulk message types onto their own lane and needs the same
        /// six-byte peek — the one parser serves both.</summary>
        internal static int PeekType(byte[] d) => d == null ? 0 : ParseType(d);

        /// <summary>The envelope is JSON with 't' first: {"t":NN,... — six bytes of prefix then digits.
        /// Anything else (a future compression marker, a fragment frame) lands in bucket 0, visibly.</summary>
        private static int ParseType(byte[] d)
        {
            // T1 compressed frame ('P') and v9 attachment frame ('A'): the type rides
            // bytes 4-5 in both, so this stays a six-byte peek.
            if (d.Length > 6 && d[0] == 0x02 && d[1] == (byte)'B' && d[2] == (byte)'Z' && (d[3] == (byte)'P' || d[3] == (byte)'A'))
            { int ft = d[4] | (d[5] << 8); return ft > 0 && ft < Slots ? ft : 0; }
            if (d.Length < 7 || d[0] != (byte)'{' || d[1] != (byte)'"' || d[2] != (byte)'t' || d[3] != (byte)'"' || d[4] != (byte)':') return 0;
            int t = 0, i = 5;
            while (i < d.Length && d[i] >= (byte)'0' && d[i] <= (byte)'9' && t < Slots) t = t * 10 + (d[i++] - (byte)'0');
            return t < Slots ? t : 0;
        }

        /// <summary>MAIN THREAD (MPCanvasUI.Update). One report line per direction every 30 s, top types
        /// by bytes — the whole audit's arithmetic, from a live session.</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextReport) return;
            bool first = _nextReport == 0f;
            _nextReport = Time.unscaledTime + ReportSeconds;
            if (first) return;   // let the first window fill before printing
            Report("OUT", _outBytes, _outMsgs);
            Report("IN ", _inBytes, _inMsgs);
        }

        private static void Report(string dir, long[] bytes, int[] msgs)
        {
            long total = 0; int totalMsgs = 0;
            var rows = new System.Collections.Generic.List<(int type, long b, int m)>();
            for (int i = 0; i < Slots; i++)
            {
                long b = System.Threading.Interlocked.Exchange(ref bytes[i], 0);
                int m = System.Threading.Interlocked.Exchange(ref msgs[i], 0);
                if (b <= 0) continue;
                total += b; totalMsgs += m;
                rows.Add((i, b, m));
            }
            if (total < QuietThreshold) return;
            rows.Sort((a, b2) => b2.b.CompareTo(a.b));
            var sb = new System.Text.StringBuilder(256);
            sb.Append("[NetStats] ").Append(dir).Append(' ').Append((int)ReportSeconds).Append("s: total ")
              .Append(Kb(total)).Append(" / ").Append(totalMsgs).Append(" msg — ");
            for (int i = 0; i < rows.Count && i < TopRows; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TypeName(rows[i].type)).Append(' ').Append(Kb(rows[i].b)).Append(" (").Append(rows[i].m).Append(')');
            }
            if (rows.Count > TopRows) sb.Append(" …+").Append(rows.Count - TopRows).Append(" type(s)");
            Plugin.Logger.LogInfo(sb.ToString());
        }

        private static string Kb(long b) => b >= 1048576 ? (b / 1048576.0).ToString("F2") + " MB" : (b / 1024.0).ToString("F1") + " KB";

        private static string TypeName(int t)
        {
            if (t == 0) return "unparsed";
            try { return ((MessageType)t).ToString(); } catch { return "type" + t; }
        }
    }
}
