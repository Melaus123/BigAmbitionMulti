using System;
using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>H-STEAMNET-2: one 1-second reading of a Steam connection, filled by SteamNetConfig.LinkWatch.
    /// Plain data - no Steamworks types - so the controller can be unit-tested with synthetic readings.</summary>
    internal sealed class SteamRateSample
    {
        public long  TimeMs;                // monotonic milliseconds
        public int   State;                 // ConnectionState: 3 = Connected (anything else is ignored)
        public int   PingMs;                // < 0 = ignored; 0 (LAN) is treated as 1 ms (a failed QuickStatus read is state 0 - rejected by State)
        public float QualityLocal  = -1f;   // 0..1, -1 = unknown
        public float QualityRemote = -1f;   // 0..1, -1 = unknown (a CHANGED value is a new report from the peer)
        public float OutBytesPerSec;        // the library's own outgoing rate
        public long  SteamPending;          // PendingReliable + PendingUnreliable (queued inside Steam, not sent)
        public long  SentUnacked;           // SentUnackedReliable (sent, not yet acknowledged)
        public long  ModBacklog;            // the mod's own retry queue + paced queue
        public long  AcceptedTotal;         // running total of reliable bytes Steam ACCEPTED from the mod
        public bool  LanesOk;               // the connection runs the three lanes (express/gameplay/bulk)
        public int   PathFlags = -1;        // SteamNetConnectionInfo_t flags word (relayed bit etc.), -1 = unknown
    }

    /// <summary>What the controller wants done. NewRate/NewBuffer 0 = unchanged. Kind: up | down | reset |
    /// floor (at the floor and still congested - reported once per link) | buffer (buffer only).</summary>
    internal sealed class SteamRateDecision
    {
        public string Kind = "", Reason = "";
        public int    OldRate, NewRate, OldBuffer, NewBuffer;
        public int    Delivery, Growth, Ceiling, BasePing;
        public double Util;
        public float  QL, QR;
        public long   Backlog;
    }

    /// <summary>H-STEAMNET-2 (user-approved 2026-09-26): the per-connection send-rate controller. PURE - no
    /// Steamworks, no Unity, no logging - so run-tests.ps1 can drive it with synthetic links.
    ///
    /// Why the mod sets the rate: Valve's library does no bandwidth estimation; with SendRateMin == SendRateMax
    /// the rate is exactly that value (with min != max it freezes a connect-time guess and never updates it), so
    /// the caller always applies min = max = Rate on the connection.
    ///
    /// Rules (design read 2026-09-26): floor 256 KB/s, ceiling 4 MB/s, start 256 KB/s, rates in 16 KB steps.
    ///  demand   = backlog (mod queues + Steam's own queue) >= 0.2 s of the rate, or the library sends >= 80 %
    ///             of the rate. Without demand nothing changes (an idle link says nothing about capacity).
    ///  delivery = bytes accepted - growth of (queued + unacked) = bytes the peer acknowledged, per second over
    ///             the last ~2 s; judged against the rate that was in force over the same window, and only once
    ///             demand has lasted 3 samples (the start of a burst would otherwise read as a slow link).
    ///             Fold F2 (2026-09-26): only SATURATED intervals count - the backlog at the interval's start was
    ///             >= rate x dt. An unsaturated interval stays out of the window and breaks that 3-sample count
    ///             (bursty sub-rate traffic says nothing about capacity).
    ///  growth   = ping - base ping (the lowest valid ping of the last 60 s).
    ///  RAISE    on 2 good samples in a row (demand, growth <= max(10 ms, 20 % base), delivery >= 85 %,
    ///           qualities >= 0.97 or unknown), >= max(2 s, 4 pings) after the last change: x1.5 with no
    ///           learned ceiling (or below half of it), x1.15 up to 90 % of it, past that a probe of
    ///           +max(32 KB/s, 5 %) only after 60 s without trouble (and 60 s after the last probe).
    ///  BACK OFF on 2 bad samples (growth > max(30 ms, 50 % base) or delivery < 70 %), 1 sample (growth >
    ///           150 ms or delivery < 50 %), or a NEW remote quality report < 0.95: rate = max(floor,
    ///           min(70 % rate, 90 % delivery)); learned ceiling = the rate the trouble appeared at; 10 s hold.
    ///  RESET    on a path change (the relayed flag bit, 16, only - fold F6): 256 KB/s, base ping and ceiling forgotten.
    ///  CEILING  a learned ceiling expires 300 s after the last trouble (fold F3), so x1.5 climbing resumes.
    ///  BUFFER   8 MB with lanes (bulk cannot block the express/gameplay lanes), else clamp(2 s of rate, 512 KB, 8 MB).</summary>
    internal sealed class SteamRateController
    {
        public const int Floor = 256 * 1024, Ceiling = 4 * 1024 * 1024, Start = Floor, Step16 = 16 * 1024;
        public const int BufferMin = 512 * 1024, BufferMax = 8 * 1024 * 1024;
        public const int ConnectedState = 3;

        // ── state ──
        public int  Rate { get; private set; } = Start;
        public int  Buffer { get; private set; } = BufferMin;   // what the connection was given at connect (512 KB)
        public int  LearnedCeiling { get; private set; }         // 0 = none
        public int  PeakRate { get; private set; } = Start;
        public int  Backoffs { get; private set; }
        public int  Raises { get; private set; }
        public int  Probes { get; private set; }
        public int  Resets { get; private set; }
        public long Samples { get; private set; }
        public long Ignored { get; private set; }
        public long FloorBusyMs { get; private set; }            // time at the floor WITH demand
        public bool FloorReported { get; private set; }
        public int  BasePing { get; private set; } = -1;
        // the last reading's figures (lever + status line)
        public int  LastDelivery { get; private set; } = -1;
        public int  LastGrowth { get; private set; }
        public double LastUtil { get; private set; }
        public float LastQL { get; private set; } = -1f;
        public float LastQR { get; private set; } = -1f;
        public long LastBacklog { get; private set; }
        public bool LastDemand { get; private set; }

        private long _prevT = -1, _prevAccepted, _prevInflight;
        private int  _pathFlags = -1;
        private float _lastQR = float.NaN;
        private long _lastChangeMs = long.MinValue / 2, _holdUntilMs = long.MinValue / 2;
        private long _lastTroubleMs = long.MinValue / 2, _lastProbeMs = long.MinValue / 2;
        private int  _goodStreak, _badStreak, _demandStreak;
        private long _prevBacklog;                                // fold F2: the backlog at the start of the interval
        private bool _congestedOnce;                              // fold F7: floorSec counts only after the first back-off/at-floor
        private readonly Queue<(long t, int ping)> _pings = new();
        private readonly List<(long end, long dt, long bytes, double rateMs)> _win = new();

        /// <summary>Feed one reading; returns a decision, or null for "nothing to do". Never throws.</summary>
        public SteamRateDecision? Step(SteamRateSample? s)
        {
            try
            {
                if (s == null) return null;
                // fold F1: only a negative ping is rejected; 0 (LAN) counts as 1 ms. A failed read is state 0.
                if (s.State != ConnectedState || s.PingMs < 0) { Ignored++; return null; }
                Samples++;
                long t = s.TimeMs;
                int ping = Math.Max(1, s.PingMs);

                // fold F3: a learned ceiling expires 300 s after the last trouble (x1.5 climbing resumes)
                if (LearnedCeiling > 0 && t - _lastTroubleMs >= 300_000) LearnedCeiling = 0;

                // path change: a different route has a different capacity - start again. Fold F6: only the
                // relayed bit (16) counts; other flag bits changing say nothing about the route.
                if (s.PathFlags >= 0)
                {
                    int was = _pathFlags;
                    _pathFlags = s.PathFlags;
                    if (was >= 0 && ((was ^ s.PathFlags) & 16) != 0) return Reset(s, $"path 0x{was:X}->0x{s.PathFlags:X}");
                }

                // base ping = lowest valid ping over the last 60 s
                _pings.Enqueue((t, ping));
                while (_pings.Count > 0 && _pings.Peek().t < t - 60_000) _pings.Dequeue();
                int bp = int.MaxValue; foreach (var p in _pings) if (p.ping < bp) bp = p.ping;
                BasePing = bp;
                int growth = ping - bp;

                long backlog = Math.Max(0, s.ModBacklog) + Math.Max(0, s.SteamPending);
                bool demand = backlog >= Rate * 0.2 || s.OutBytesPerSec >= 0.8 * Rate;
                LastGrowth = growth; LastQL = s.QualityLocal; LastQR = s.QualityRemote; LastBacklog = backlog; LastDemand = demand;
                LastUtil = Rate > 0 ? s.OutBytesPerSec / (double)Rate : 0;
                bool newRemote = s.QualityRemote >= 0 && !float.IsNaN(_lastQR) && s.QualityRemote != _lastQR;
                _lastQR = s.QualityRemote;

                long inflight = Math.Max(0, s.SteamPending) + Math.Max(0, s.SentUnacked);
                // first reading, a gap (> 5 s) or a clock step: a new baseline, no judgement
                if (_prevT < 0 || t <= _prevT || t - _prevT > 5000)
                {
                    _prevT = t; _prevAccepted = s.AcceptedTotal; _prevInflight = inflight; _prevBacklog = backlog;
                    _win.Clear(); _demandStreak = demand ? 1 : 0; _goodStreak = 0; _badStreak = 0;
                    return BufferOnly(s);
                }
                long dt = t - _prevT;
                long delivered = Math.Max(0, (s.AcceptedTotal - _prevAccepted) - (inflight - _prevInflight));
                // fold F2: the interval was SATURATED when the backlog at its start could fill it at the rate
                bool saturated = _prevBacklog >= (double)Rate * dt / 1000.0;
                _prevT = t; _prevAccepted = s.AcceptedTotal; _prevInflight = inflight; _prevBacklog = backlog;
                if (saturated) _win.Add((t, dt, delivered, (double)Rate * dt));   // the rate in force over this interval
                _win.RemoveAll(w => w.end <= t - 2000);
                long sdt = 0, sb = 0; double srate = 0;
                foreach (var w in _win) { sdt += w.dt; sb += w.bytes; srate += w.rateMs; }
                double dlv = sdt > 0 ? sb * 1000.0 / sdt : 0;
                double winRate = sdt > 0 ? srate / sdt : Rate;
                LastDelivery = sdt > 0 ? (int)dlv : -1;
                _demandStreak = demand && saturated ? _demandStreak + 1 : 0;   // F2: an unsaturated interval breaks it
                bool dlvValid = _demandStreak >= 3 && sdt > 0;
                double ratio = winRate > 0 ? dlv / winRate : 1;
                if (demand && Rate <= Floor && _congestedOnce) FloorBusyMs += dt;   // F7: only after the first back-off/at-floor

                if (t < _holdUntilMs) { _goodStreak = 0; _badStreak = 0; return BufferOnly(s); }   // 10 s hold after a back-off
                if (!demand) { _goodStreak = 0; _badStreak = 0; return BufferOnly(s); }            // no demand: hold

                bool hard = growth > 150 || (dlvValid && ratio < 0.5);
                bool soft = growth > Math.Max(30, 0.5 * bp) || (dlvValid && ratio < 0.7);
                bool qualBad = newRemote && s.QualityRemote < 0.95f;
                if (hard || soft || qualBad) { _badStreak++; _goodStreak = 0; _lastTroubleMs = t; }
                else _badStreak = 0;
                if (hard || qualBad || _badStreak >= 2)
                    return BackOff(s, dlvValid ? dlv : -1,
                        hard ? (growth > 150 ? $"growth {growth}ms" : $"delivery {ratio:P0}")
                             : qualBad ? $"remote quality {s.QualityRemote:F2}"
                             : (growth > Math.Max(30, 0.5 * bp) ? $"growth {growth}ms x2" : $"delivery {ratio:P0} x2"));

                bool qOk = (s.QualityLocal < 0 || s.QualityLocal >= 0.97f) && (s.QualityRemote < 0 || s.QualityRemote >= 0.97f);
                bool good = dlvValid && growth <= Math.Max(10, 0.2 * bp) && ratio >= 0.85 && qOk;
                _goodStreak = good ? _goodStreak + 1 : 0;
                if (_goodStreak >= 2 && t - _lastChangeMs >= Math.Max(2000L, 4L * ping))
                    return Raise(s);
                return BufferOnly(s);
            }
            catch { return null; }
        }

        private SteamRateDecision? Raise(SteamRateSample s)
        {
            long t = s.TimeMs;
            int old = Rate, target; string why; bool probe = false;
            if (LearnedCeiling <= 0 || old < LearnedCeiling / 2)
            {
                double x = old * 1.5;
                if (LearnedCeiling > 0) x = Math.Min(x, 0.9 * LearnedCeiling);
                target = LearnedCeiling > 0 ? FloorTo16(x) : Round16(x); why = "x1.5";
            }
            else
            {
                int capped = FloorTo16(Math.Min(old * 1.15, 0.9 * LearnedCeiling));
                if (capped > old) { target = capped; why = "x1.15"; }
                else
                {
                    if (t - Math.Max(_lastTroubleMs, _lastProbeMs) < 60_000) return BufferOnly(s);   // probe: 60 s clean first
                    target = Round16(old + Math.Max(32 * 1024, old * 0.05)); why = "probe"; probe = true;
                }
            }
            target = Math.Min(Ceiling, Math.Max(Floor, target));
            if (target <= old) return BufferOnly(s);   // at the 4 MB/s ceiling
            if (probe) { _lastProbeMs = t; Probes++; }   // fold F7: only a probe that actually changes the rate counts
            if (LearnedCeiling > 0 && target > LearnedCeiling) LearnedCeiling = target;   // a clean probe moves the learned ceiling
            Rate = target; Raises++; _lastChangeMs = t; _goodStreak = 0;
            if (Rate > PeakRate) PeakRate = Rate;
            return Finish(s, "up", why, old);
        }

        private SteamRateDecision? BackOff(SteamRateSample s, double dlv, string why)
        {
            long t = s.TimeMs;
            int old = Rate;
            double x = 0.7 * old;
            if (dlv >= 0) x = Math.Min(x, 0.9 * dlv);
            int target = Math.Max(Floor, FloorTo16(x));
            _holdUntilMs = t + 10_000; _goodStreak = 0; _badStreak = 0; _lastTroubleMs = t; _congestedOnce = true;
            if (target >= old)
            {
                // already at the floor: nothing lower to go to - report it once per link
                if (FloorReported) return BufferOnly(s);
                FloorReported = true;
                return Finish(s, "floor", why, old);
            }
            LearnedCeiling = old;
            Rate = target; Backoffs++; _lastChangeMs = t;
            return Finish(s, "down", why, old);
        }

        private SteamRateDecision Reset(SteamRateSample s, string why)
        {
            int old = Rate;
            Rate = Start; LearnedCeiling = 0; Resets++;
            _pings.Clear(); BasePing = -1; _win.Clear();
            _prevT = s.TimeMs; _prevAccepted = s.AcceptedTotal; _prevInflight = Math.Max(0, s.SteamPending) + Math.Max(0, s.SentUnacked);
            _prevBacklog = Math.Max(0, s.ModBacklog) + Math.Max(0, s.SteamPending);
            _goodStreak = 0; _badStreak = 0; _demandStreak = 0; _lastChangeMs = s.TimeMs; _holdUntilMs = long.MinValue / 2;
            _lastTroubleMs = long.MinValue / 2; _lastProbeMs = long.MinValue / 2;
            var d = Finish(s, "reset", why, old);
            d.NewRate = Start;   // always re-applied (idempotent) - the path changed under it
            return d;
        }

        private SteamRateDecision? BufferOnly(SteamRateSample s)
        {
            int want = WantedBuffer(s.LanesOk);
            return want == Buffer ? null : Finish(s, "buffer", s.LanesOk ? "lanes" : "no lanes", Rate);
        }

        private SteamRateDecision Finish(SteamRateSample s, string kind, string why, int oldRate)
        {
            var d = new SteamRateDecision
            {
                Kind = kind, Reason = why, OldRate = oldRate, NewRate = Rate != oldRate ? Rate : 0,
                OldBuffer = Buffer, Delivery = LastDelivery, Growth = LastGrowth, Ceiling = LearnedCeiling,
                BasePing = BasePing, Util = LastUtil, QL = LastQL, QR = LastQR, Backlog = LastBacklog,
            };
            int want = WantedBuffer(s.LanesOk);
            if (want != Buffer) { d.NewBuffer = want; Buffer = want; }
            return d;
        }

        public int WantedBuffer(bool lanesOk)
            => lanesOk ? BufferMax : Math.Min(BufferMax, Math.Max(BufferMin, Round16(Rate * 2.0)));

        public static int Round16(double x) => (int)Math.Round(x / Step16) * Step16;
        public static int FloorTo16(double x) => (int)Math.Floor(x / Step16) * Step16;

        /// <summary>One line of state for the DEV lever.</summary>
        public string Describe()
            => $"rate={Rate / 1024}K ceil={LearnedCeiling / 1024}K buffer={Buffer / 1024}K base={BasePing} dlv={(LastDelivery < 0 ? "?" : (LastDelivery / 1024).ToString())}K "
             + $"growth={LastGrowth} util={LastUtil:F2} qL={LastQL:F2} qR={LastQR:F2} backlog={LastBacklog / 1024}K demand={LastDemand} "
             + $"up={Raises} probes={Probes} down={Backoffs} resets={Resets} peak={PeakRate / 1024}K floorSec={FloorBusyMs / 1000} samples={Samples} ignored={Ignored}";
    }
}
