#nullable disable
using BigAmbitions.Rivals;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>H-RIVALPARITY-1 part A (user-approved 2026-09-27): one row of per-player rival ATTENTION as the MP
    /// manifest carries it. Key = "p:&lt;StableId&gt;" for a single player or "g:&lt;groupId&gt;" for a merged company.
    /// IsHostKey rows are the host's own NATIVE state (gi.specialRivalStates) as of the save moment.</summary>
    public class MpRivalAttnEntry
    {
        public string Key { get; set; } = "";
        public string RivalId { get; set; } = "";
        public bool IsActive { get; set; }
        public bool IsHostKey { get; set; }
        public bool CopycatDone { get; set; }
        public bool EverCounted { get; set; }
        public List<string> Completed { get; set; } = new List<string>();
        public List<string> Sent { get; set; } = new List<string>();
        /// <summary>H-RIVALPARITY-1 part B: WORLD rows (Key = "*world", one per rival) carry the running attacks with
        /// their attribution, the price-war queue and the copycat shops added so far. Null / 0 on every other row.</summary>
        public List<MpRivalAttack> Attacks { get; set; }
        public List<MpRivalQueued> Queue { get; set; }
        public int CopycatAdded { get; set; }
    }

    /// <summary>H-RIVALPARITY-1 part B: one pre-war price of an item a war cut (the restore check's reference).</summary>
    public class MpRivalWarItem
    {
        public string Addr { get; set; } = "";
        public string Item { get; set; } = "";
        public float Pre { get; set; }
    }

    /// <summary>H-RIVALPARITY-1 part B: one running attack and the key it is aimed at. EndMin = the native DefenseState's
    /// end (Timestamp.GetTotalMinutes, rounded); with the rival and the mechanic it identifies the native state.</summary>
    public class MpRivalAttack
    {
        public string Key { get; set; } = "";
        public string RivalId { get; set; } = "";
        public string Nb { get; set; } = "";
        public string Mechanic { get; set; } = "";
        public int Aggression { get; set; }
        public string EntryId { get; set; } = "";
        public int EndMin { get; set; }
        public float Cut { get; set; }
        public List<MpRivalWarItem> Items { get; set; } = new List<MpRivalWarItem>();
        public List<string> Opened { get; set; } = new List<string>();
    }

    /// <summary>H-RIVALPARITY-1 part B: a price war waiting for the running one (first in, first out, per rival).</summary>
    public class MpRivalQueued
    {
        public string Key { get; set; } = "";
        public string EntryId { get; set; } = "";
        public int Aggression { get; set; }
        public int AtMin { get; set; }
        public int LastTryMin { get; set; }
    }

    /// <summary>
    /// H-RIVALPARITY-1 part A: PER-PLAYER RIVAL ATTENTION (design_rivals.md (a)(b)(h)(i)).
    ///
    /// The game's own rival timeline (RivalTimeline.Check, decompile RivalTimeline.cs:99-124) keeps running
    /// UNCHANGED on the host and speaks for the HOST KEY only - its state is the native gi.specialRivalStates.
    /// Every OTHER key (a player, or a merged company = one key) with at least one online member gets the same
    /// check replayed here on the host after each native sweep (postfix on RivalsHelper.CheckRivalTimelines /
    /// CheckRivalTimeline(nb)), step by step as native does it:
    ///   count/income  - the key's OWN qualifying shops in the rival's neighbourhood, each qualifying test run on
    ///                   the OWNER's machine with the exact native GetPlayerValues test (:283-301) and carried in
    ///                   the self-report row (RivalBusinessInfo.RivalQualifying) - no pooling, no divide-by-N;
    ///   intro         - :126-132 (entrance message once, 10-30 s later, text to the key's online members);
    ///   activation    - :165-203 (only once the intro was sent; &lt;3 deactivates, &gt;=3 activates, after the delay);
    ///   surrender     - :134-163 (the host runs RivalsHelper.DefeatRival once, for everybody);
    ///   triggers      - :205-239 (same thresholds, same planning times).
    /// PART B (2026-09-27) MAKES THE ATTACKS REAL: a due entry fires through the game's own ActivatePriceReduction /
    /// ActivateLowDemand aimed at the key (Target), is marked completed only on success, and its special message is
    /// taken off the shared SpecialMessagesTmpQueue and routed to the key as text. Poaching stays OFF.
    /// SOLO HOST: with no other key online the postfix returns before doing anything - the native path alone runs.
    /// Host-only; main thread (the sweep postfix, the 3 s heartbeat tick, the rig levers).
    /// </summary>
    public static class MPRivalAttention
    {
        private sealed class State
        {
            public bool IsActive, CopycatDone, EverCounted;
            public List<string> Completed = new List<string>();
            public List<string> Sent = new List<string>();
        }

        private sealed class Planned
        {
            public string Key, RivalId, EntryId, Mechanic;
            public int DueMin;
        }

        private sealed class Pending
        {
            public float Due;
            public string Tag;
            public System.Action Run;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, State> _store = new Dictionary<string, State>(System.StringComparer.Ordinal);   // key|rivalId
        private static readonly List<Planned> _planned = new List<Planned>();
        private static readonly List<Pending> _pending = new List<Pending>();
        private static readonly Dictionary<string, int> _extra = new Dictionary<string, int>(System.StringComparer.Ordinal);      // DEV lever: key|rivalId -> count floor (rivalattn floor)
        // H-RIVALPARITY-1 part B world data (host; written on the main thread under _lock; persisted as "*world" rows)
        private static readonly List<MpRivalAttack> _attacks = new List<MpRivalAttack>();
        private static readonly Dictionary<string, List<MpRivalQueued>> _warQ = new Dictionary<string, List<MpRivalQueued>>(System.StringComparer.Ordinal);   // rivalId -> FIFO
        private static readonly Dictionary<string, int> _copyAdded = new Dictionary<string, int>(System.StringComparer.Ordinal);   // rivalId -> copycat shops added by every wave so far
        private static readonly HashSet<string> _hostCopycat = new HashSet<string>(System.StringComparer.Ordinal);                 // rivalId: the HOST key's once-per-key copycat flag
        internal const string WorldKey = "*world";
        private const int LowDemandTotalCap = 10;        // the game's own LowDemandNewBusinessesCap (RivalDefenseHelper.cs:26), cumulative per neighbourhood
        private const int StaleQueueMin = 48 * 60;       // a queued war not retried for 48 game hours leaves the head
        private static List<MpRivalAttnEntry> _hostRowsCache = new List<MpRivalAttnEntry>();
        private static List<MpRivalAttnEntry> _manifestHostRows;
        private static string _manifestHostKey = "";
        private static object _boundGi, _giAtRestore;
        private static bool _armed, _migrationPending, _inSweep;
        /// <summary>Mod-path attack firings (dry run in part A) since this process started - the solo-host oracle.</summary>
        internal static int ModPathFirings;

        // ── keys ────────────────────────────────────────────────────────────────────────────────
        internal static string KeyOfStable(string stable)
        {
            if (string.IsNullOrEmpty(stable)) return "";
            string g = "";
            try { g = MergerSync.GroupOfStable(stable) ?? ""; } catch { }
            return g.Length > 0 ? "g:" + g : "p:" + stable;
        }

        internal static string StableOfPid(string pid)
        {
            if (string.IsNullOrEmpty(pid)) return "";
            if (pid == MPConfig.PlayerId) return MPConfig.StableId ?? "";
            try { if (MPServer.StableIdByPlayer.TryGetValue(pid, out var s)) return s ?? ""; } catch { }
            return "";
        }

        internal static string KeyOfPid(string pid) => KeyOfStable(StableOfPid(pid));

        internal static string HostKey => KeyOfStable(MPConfig.StableId);

        private static List<string> StablesOfKey(string key)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(key) || key.Length < 3) return list;
            string id = key.Substring(2);
            if (key.StartsWith("p:", System.StringComparison.Ordinal)) { list.Add(id); return list; }
            try { foreach (var s in MergerSync.JoinOrderOfGroup(id)) if (!string.IsNullOrEmpty(s) && !list.Contains(s)) list.Add(s); } catch { }
            try { if (MergerSync.StoreGroups.TryGetValue(id, out var set) && set != null) foreach (var s in set) if (!string.IsNullOrEmpty(s) && !list.Contains(s)) list.Add(s); } catch { }
            return list;
        }

        /// <summary>Every pid this host knows for a stable id (online or not - a self-report outlives a drop).</summary>
        private static List<string> PidsOfStable(string stable)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(stable)) return list;
            if (stable == MPConfig.StableId) list.Add(MPConfig.PlayerId);
            try { foreach (var kv in MPServer.StableIdByPlayer) if (kv.Value == stable && !list.Contains(kv.Key)) list.Add(kv.Key); } catch { }
            return list;
        }

        private static List<string> OnlineNonHostPids()
        {
            var list = new List<string>();
            try
            {
                var all = new List<string>(MPServer.LobbyPlayers);
                foreach (var pid in all)
                    if (!string.IsNullOrEmpty(pid) && pid != MPConfig.PlayerId && MPServer.IsOnlinePid(pid) && !list.Contains(pid)) list.Add(pid);
            }
            catch { }
            return list;
        }

        /// <summary>The ONLINE members of a key, the host included when it is one.</summary>
        internal static List<string> OnlinePidsOfKey(string key)
        {
            var list = new List<string>();
            try
            {
                var stables = StablesOfKey(key);
                if (stables.Contains(MPConfig.StableId)) list.Add(MPConfig.PlayerId);
                foreach (var pid in OnlineNonHostPids())
                    if (stables.Contains(StableOfPid(pid)) && !list.Contains(pid)) list.Add(pid);
            }
            catch { }
            return list;
        }

        /// <summary>Text routing for the host's OWN (native) rival path: the host key's online members.</summary>
        internal static List<string> HostKeyOnlineMembers() => OnlinePidsOfKey(HostKey);

        /// <summary>Distinct keys of the online non-host players, the host key excluded.</summary>
        private static List<string> NonHostKeysOnline()
        {
            var keys = new List<string>();
            string hk = HostKey;
            foreach (var pid in OnlineNonHostPids())
            {
                string k = KeyOfPid(pid);
                if (k.Length == 0 || k == hk || keys.Contains(k)) continue;
                keys.Add(k);
            }
            return keys;
        }

        private static State Get(string key, string rivalId, bool create)
        {
            lock (_lock)
            {
                string k = key + "|" + rivalId;
                if (_store.TryGetValue(k, out var st)) return st;
                if (!create) return null;
                st = new State();
                _store[k] = st;
                return st;
            }
        }

        // ── counting (P3) ───────────────────────────────────────────────────────────────────────
        /// <summary>Fold R6 (manager ruling 2026-09-27, replaces part D F8's 10-minute cut-off): single-player parity -
        /// a member's shops do not vanish while that member is offline. Every member of the key counts through its
        /// LAST report, with no expiry; the rows stop counting for a company only when the member leaves it (the
        /// member's stable is no longer in StablesOfKey). A member known under several pids counts through its
        /// online pid(s) that have reported, else through the pid with the freshest report (never a stale older pid).
        /// After a host restart the rows come back when the member reports again.</summary>
        private static List<string> CountingPidsOfStable(string stable)
        {
            var result = new List<string>();
            try
            {
                var all = PidsOfStable(stable);
                foreach (var pid in all)
                {
                    bool online = pid == MPConfig.PlayerId;
                    try { online |= MPServer.IsOnlinePid(pid); } catch { }
                    if (online && MPServer.SelfReportAgeSeconds(pid) < double.MaxValue) result.Add(pid);
                }
                if (result.Count > 0) return result;
                string best = ""; double bestAge = double.MaxValue;
                foreach (var pid in all)
                {
                    double age = MPServer.SelfReportAgeSeconds(pid);
                    if (age < bestAge) { bestAge = age; best = pid; }
                }
                if (best.Length > 0) result.Add(best);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] count pids of '{stable}': {ex.Message}"); }
            return result;
        }

        /// <summary>The key's OWN qualifying shops in the rival's neighbourhood and their weekly income, from the
        /// owners' self-report rows (RivalQualifying = the native GetPlayerValues test run on the owner's machine).
        /// The host's own shops are never counted here - the host key is the native path.</summary>
        internal static void CountFor(string key, SpecialRival rival, out int count, out float income, out int extra)
        {
            count = 0; income = 0f; extra = 0;
            try
            {
                string nb = rival?.primaryNeighborhood ?? "";
                string rid = rival?.rivalData?.id ?? "";
                var seen = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var stable in StablesOfKey(key))
                {
                    if (stable == MPConfig.StableId) continue;
                    foreach (var pid in CountingPidsOfStable(stable))   // fold R6: an offline member's last report counts, no expiry
                    {
                        foreach (var row in MPServer.SelfReportRows(pid))
                        {
                            if (row == null || !row.RivalQualifying || row.Neighborhood != nb) continue;
                            if (!seen.Add(row.AddressKey ?? "")) continue;
                            count++;
                            income += row.WeeklyIncome;
                        }
                    }
                }
                lock (_lock) _extra.TryGetValue(key + "|" + rid, out extra);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] count {key}: {ex.Message}"); }
        }

        // ── lifecycle / persistence ─────────────────────────────────────────────────────────────
        /// <summary>Bind the store to the loaded world. A world that was NOT restored from a manifest (a new world)
        /// starts empty. Returns false while the host is still between a manifest restore and its world load.</summary>
        private static bool EnsureBound()
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return false;
            if (ReferenceEquals(gi, _boundGi))
            {
                if (_manifestHostRows != null || _migrationPending) SettleLoad();   // part D F4: a settle deferred at the bind
                return true;
            }
            lock (_lock)
            {
                if (_armed)
                {
                    if (ReferenceEquals(gi, _giAtRestore)) return false;   // the pre-load world is still current
                    _armed = false;
                }
                else
                {
                    _store.Clear(); _planned.Clear(); _pending.Clear(); _extra.Clear(); ClearWorldLocked();
                    _manifestHostRows = null; _manifestHostKey = "";
                    _migrationPending = true;
                }
                _boundGi = gi;
            }
            SettleLoad();   // part D F4: settle at the bind, not at the first sweep - a manifest write in between must not drop the previous host's rows
            return true;
        }

        /// <summary>MPSaveCoordinator load path, beside RestoreOwnershipFromManifest: clear-then-apply from THIS
        /// manifest (same timeline rule as the absence marks). NULL = a manifest older than this feature =
        /// migration: the host key IS the native state already; every other player starts fresh.</summary>
        public static void RestoreFromManifest(MpManifest m)
        {
            try
            {
                int n = 0, keys = 0, host = 0, world = 0;
                lock (_lock)
                {
                    _store.Clear(); _planned.Clear(); _pending.Clear(); _extra.Clear(); ClearWorldLocked();
                    _manifestHostRows = null; _manifestHostKey = ""; _migrationPending = false;
                    _hostRowsCache = new List<MpRivalAttnEntry>();   // part D F4: the previous world's host rows must never be written for this one
                    var seenKeys = new HashSet<string>(System.StringComparer.Ordinal);
                    if (m?.RivalAttention == null) _migrationPending = true;
                    else
                        foreach (var e in m.RivalAttention)
                        {
                            if (e == null || string.IsNullOrEmpty(e.Key) || string.IsNullOrEmpty(e.RivalId)) continue;
                            if (e.Key == WorldKey) { ApplyWorldRowLocked(e); world++; continue; }   // part B: attacks, queue, copycat count
                            if (e.IsHostKey)
                            {
                                if (e.CopycatDone) _hostCopycat.Add(e.RivalId);   // part B (a host change re-reads it in SettleLoad)
                                if (_manifestHostRows == null) _manifestHostRows = new List<MpRivalAttnEntry>();
                                _manifestHostRows.Add(e); _manifestHostKey = e.Key; host++;
                                continue;
                            }
                            _store[e.Key + "|" + e.RivalId] = new State
                            {
                                IsActive = e.IsActive, CopycatDone = e.CopycatDone, EverCounted = e.EverCounted,
                                Completed = new List<string>(e.Completed ?? new List<string>()),
                                Sent = new List<string>(e.Sent ?? new List<string>()),
                            };
                            n++;
                            if (seenKeys.Add(e.Key)) keys++;
                        }
                    _armed = true;
                    _boundGi = null;
                    _giAtRestore = SaveGameManager.Current;
                }
                if (m?.RivalAttention == null)
                    Plugin.Logger.LogInfo("[RivalAttn] manifest predates per-player rival attention - migration at world load (host key from native state, every other player starts fresh).");
                else
                    Plugin.Logger.LogInfo($"[RivalAttn] restored {n} key-rival row(s) for {keys} key(s) from the manifest (+{host} host row(s), host key '{_manifestHostKey}'; {world} world row(s): {_attacks.Count} running attack(s), {QueuedCountLocked()} queued war(s)).");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] manifest restore: {ex.Message}"); }
        }

        /// <summary>The manifest's copy (host rows from the main-thread cache + every other key). refreshHost reads the
        /// native state now - only from a main-thread caller.</summary>
        public static List<MpRivalAttnEntry> Snapshot(bool refreshHost)
        {
            var list = new List<MpRivalAttnEntry>();
            try
            {
                if (refreshHost) { EnsureBound(); RefreshHostRowsCache(); }   // part D F4: main-thread caller - bind (and settle) first
                string hkNow = HostKey;
                lock (_lock)
                {
                    foreach (var h in _hostRowsCache)
                        list.Add(new MpRivalAttnEntry { Key = h.Key, RivalId = h.RivalId, IsActive = h.IsActive, IsHostKey = true, CopycatDone = _hostCopycat.Contains(h.RivalId),
                                                        Completed = new List<string>(h.Completed), Sent = new List<string>(h.Sent) });
                    // part D F4: manifest host rows not settled yet travel on unchanged in meaning - the previous host's
                    // rows as an ordinary player key (what SettleLoad turns them into), this host's own as host rows
                    // only while no native read exists.
                    if (_manifestHostRows != null)
                    {
                        bool other = _manifestHostKey != hkNow;
                        if (other || _hostRowsCache.Count == 0)
                            foreach (var e in _manifestHostRows)
                            {
                                if (e == null) continue;
                                if (other && _store.ContainsKey(e.Key + "|" + e.RivalId)) continue;
                                list.Add(new MpRivalAttnEntry { Key = e.Key, RivalId = e.RivalId, IsActive = e.IsActive, IsHostKey = !other, EverCounted = other && EntranceIn(e.RivalId, e.Sent), CopycatDone = e.CopycatDone,
                                                                Completed = new List<string>(e.Completed ?? new List<string>()), Sent = new List<string>(e.Sent ?? new List<string>()) });
                            }
                    }
                    foreach (var kv in _store)
                    {
                        int bar = kv.Key.LastIndexOf('|');
                        if (bar <= 0) continue;
                        var st = kv.Value;
                        list.Add(new MpRivalAttnEntry { Key = kv.Key.Substring(0, bar), RivalId = kv.Key.Substring(bar + 1), IsActive = st.IsActive,
                                                        CopycatDone = st.CopycatDone, EverCounted = st.EverCounted,
                                                        Completed = new List<string>(st.Completed), Sent = new List<string>(st.Sent) });
                    }
                    list.AddRange(WorldRowsLocked());   // part B
                }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] snapshot: {ex.Message}"); }
            return list;
        }

        private static void RefreshHostRowsCache()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.specialRivalStates == null || !MPServer.IsRunning) return;
                if (!ReferenceEquals(gi, _boundGi)) return;
                string hk = HostKey;
                var rows = new List<MpRivalAttnEntry>();
                var seen = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var st in gi.specialRivalStates)
                {
                    if (st == null || string.IsNullOrEmpty(st.rivalId) || !seen.Add(st.rivalId)) continue;   // the game reads the FIRST per id
                    rows.Add(new MpRivalAttnEntry { Key = hk, RivalId = st.rivalId, IsActive = st.isActive, IsHostKey = true,
                                                    Completed = new List<string>(st.completedTimelineEntryIds ?? new List<string>()),
                                                    Sent = new List<string>(st.sentMessageKeys ?? new List<string>()) });
                }
                lock (_lock) _hostRowsCache = rows;
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] host rows: {ex.Message}"); }
        }

        /// <summary>First sweep after a load: log the migration, or settle a HOST CHANGE - the manifest's host rows
        /// then belong to the previous host (now an ordinary key), and this host's stored key values (from when it
        /// was a client) are copied into the native state IN PLACE.</summary>
        private static void SettleLoad()
        {
            try
            {
                string hk = HostKey;
                if (_migrationPending)
                {
                    _migrationPending = false;
                    int a = 0, c = 0, s = 0, d = 0, r = 0;
                    var gi = SaveGameManager.Current;
                    var seen = new HashSet<string>(System.StringComparer.Ordinal);
                    if (gi?.specialRivalStates != null)
                        foreach (var st in gi.specialRivalStates)
                        {
                            if (st == null || string.IsNullOrEmpty(st.rivalId) || !seen.Add(st.rivalId)) continue;
                            r++; if (st.isActive) a++;
                            c += st.completedTimelineEntryIds?.Count ?? 0; s += st.sentMessageKeys?.Count ?? 0; d += st.defenseStates?.Count ?? 0;
                        }
                    Plugin.Logger.LogInfo($"[RivalAttn] migration: host key '{hk}' seeded from native state ({r} rival(s), {a} active, {c} completed, {s} sent, {d} running attack(s) credited to the host key); every other player starts fresh.");
                }
                List<MpRivalAttnEntry> hostRows;
                string mk;
                lock (_lock) { hostRows = _manifestHostRows; mk = _manifestHostKey; }
                if (hostRows == null) return;
                if (mk != hk)
                {
                    var gi2 = SaveGameManager.Current;
                    if (gi2?.specialRivalStates == null || gi2.specialRivalStates.Count == 0) return;   // part D F4: the world's rival states are not loaded yet - settle on the next call (Snapshot keeps the rows meanwhile)
                }
                lock (_lock) _manifestHostRows = null;
                if (mk == hk) return;
                // A different person hosted when this manifest was written.
                lock (_lock)
                    foreach (var e in hostRows)
                        _store[e.Key + "|" + e.RivalId] = new State { IsActive = e.IsActive, EverCounted = EntranceIn(e.RivalId, e.Sent), CopycatDone = e.CopycatDone,   // fold B1
                                                                      Completed = new List<string>(e.Completed ?? new List<string>()),
                                                                      Sent = new List<string>(e.Sent ?? new List<string>()) };
                int moved = 0;
                lock (_lock) _hostCopycat.Clear();   // part B: the manifest's host flags were the previous host's (now in its store rows)
                foreach (var rival in RivalsHelper.GetSpecialRivals())
                {
                    string rid = rival?.rivalData?.id ?? "";
                    if (rid.Length == 0) continue;
                    var mine = Get(hk, rid, false);
                    var nst = RivalsHelper.GetSpecialRivalState(rid);
                    if (nst == null) continue;
                    bool act = mine?.IsActive ?? false;
                    var comp = mine?.Completed ?? new List<string>();
                    var sent = mine?.Sent ?? new List<string>();
                    if (!nst.isDefeated) nst.isActive = act;
                    if (nst.completedTimelineEntryIds == null) nst.completedTimelineEntryIds = new List<string>();
                    nst.completedTimelineEntryIds.Clear(); nst.completedTimelineEntryIds.AddRange(comp);
                    if (nst.sentMessageKeys == null) nst.sentMessageKeys = new List<string>();
                    nst.sentMessageKeys.Clear(); nst.sentMessageKeys.AddRange(sent);
                    lock (_lock) { if (mine != null && mine.CopycatDone) _hostCopycat.Add(rid); _store.Remove(hk + "|" + rid); }
                    moved++;
                }
                Plugin.Logger.LogInfo($"[RivalAttn] host change: previous host key '{mk}' kept as a player key; this host's key '{hk}' copied into the native state for {moved} rival(s).");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] settle load: {ex.Message}"); }
        }

        // ── the replayed timeline check (b) ─────────────────────────────────────────────────────
        /// <summary>Postfix body: after the native sweep (nb = null) or the per-neighbourhood check.</summary>
        internal static void AfterNativeSweep(string nb)
        {
            if (!MPServer.IsRunning || _inSweep) return;
            _inSweep = true;
            try
            {
                if (!EnsureBound()) return;
                SettleLoad();
                if (!RivalsHelper.IsFeatureEnabled) return;
                var keys = NonHostKeysOnline();
                if (keys.Count == 0) return;                           // solo host: the native path alone
                foreach (var rival in RivalsHelper.GetSpecialRivals())
                {
                    if (rival == null || rival.rivalData == null) continue;
                    if (!string.IsNullOrEmpty(nb) && rival.primaryNeighborhood != nb) continue;
                    foreach (var k in keys)
                    {
                        try { CheckKey(k, rival); }
                        catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] check {k} on {rival.rivalData.id}: {ex.GetType().Name}: {ex.Message}"); }
                    }
                }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] sweep: {ex.GetType().Name}: {ex.Message}"); }
            finally { _inSweep = false; }
        }

        /// <summary>RivalTimeline.Check (:99-124) for one non-host key.</summary>
        private static void CheckKey(string key, SpecialRival rival)
        {
            string rid = rival.rivalData.id ?? "";
            if (rid.Length == 0) return;
            var nst = RivalsHelper.GetSpecialRivalState(rid);
            if (nst == null || nst.isDefeated) return;
            CountFor(key, rival, out int count, out float income, out int extra);
            int n = System.Math.Max(count, extra);   // DEV floor (rivalattn floor), 0 = the real count
            if (n <= 0) return;
            var st = Get(key, rid, true);
            bool entranceSent;
            lock (_lock) { st.EverCounted = true; entranceSent = st.Sent.Contains(rival.entranceMessageKey ?? ""); }
            var tl = rival.timeline;

            // CheckEntranceMessage (:126-132)
            if (!entranceSent)
                Schedule($"{key}|{rid}|intro", () => DeliverOnce(key, rival, rival.entranceMessageKey, "intro"));

            // CheckActivation (:165-185) - only once the intro was ALREADY sent before this check
            if (entranceSent && tl != null)
            {
                bool active;
                lock (_lock) active = st.IsActive;
                if (active && n < 3)
                    Schedule($"{key}|{rid}|deact", () => ActivationStep(key, rival, false));
                else if (!active && n >= 3)
                    Schedule($"{key}|{rid}|act", () => ActivationStep(key, rival, true));
            }

            bool isActive;
            lock (_lock) isActive = st.IsActive;
            if (isActive && !CheckSurrender(rival, nst))
                CheckTriggers(key, rival, n, income);
        }

        /// <summary>A delayed step, 10-30 s like SendMessageToPlayerDelayed (RivalsHelper.cs:151-157). One per tag.</summary>
        private static void Schedule(string tag, System.Action run)
        {
            lock (_lock)
            {
                foreach (var p in _pending) if (p.Tag == tag) return;
                _pending.Add(new Pending { Tag = tag, Run = run, Due = UnityEngine.Time.realtimeSinceStartup + UnityEngine.Random.Range(10, 30) });
            }
        }

        private static bool DeliverOnce(string key, SpecialRival rival, string msgKey, string what) => DeliverOnce(key, rival, msgKey, what, null, out _);

        /// <summary>SendMessageToPlayer (:164-211) for a key: a key already in Sent only runs the follow-up (true);
        /// otherwise ONE 'rivalmono' goes to the key's online members (part C: each plays the rival's own monologue for it,
        /// then raises the contact message and the trailing messages - native's callback order and read flags) and the key
        /// is marked sent. The host itself plays nothing: its follow-ups run directly after this returns.
        /// Nobody online = nothing sent, nothing marked, no follow-up - the next check tries again.</summary>
        private static bool DeliverOnce(string key, SpecialRival rival, string msgKey, string what, List<RivalMonoTrailing> trailing, out int carried)
        {
            carried = 0;
            try
            {
                if (string.IsNullOrEmpty(msgKey)) return true;
                string rid = rival.rivalData?.id ?? "";
                var st = Get(key, rid, true);
                lock (_lock) { if (st.Sent.Contains(msgKey)) return true; }
                var pids = OnlinePidsOfKey(key);
                pids.Remove(MPConfig.PlayerId);
                var clip = CompanyMessages.RivalClipFor(rival, msgKey, out string where);
                bool hasClip = clip != null;
                // read=false: that is native's flag only when there is NO clip (SendMessageWithoutNotification, :613-615);
                // a receiver that plays the monologue raises it read, as the monologue callback does (:588).
                int n = CompanyMessages.SendRivalMonoToPids(rival, msgKey, null, false, false, trailing, pids);
                if (n == 0)
                {
                    Plugin.Logger.LogInfo($"[RivalAttn] {what} '{msgKey}' for {key} on {rid}: no member online - not sent, retried on the next check.");
                    return false;
                }
                lock (_lock) st.Sent.Add(msgKey);
                carried = trailing != null && trailing.Count > 0 ? n : 0;
                Plugin.Logger.LogInfo($"[RivalAttn] {what} '{msgKey}' for {key} on {rid}: rivalmono to {n} player(s) [{string.Join(",", pids)}] (clip={(hasClip ? where : "none - plain text")}, trailing={trailing?.Count ?? 0}).");
                return true;
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] {what} for {key}: {ex.Message}"); return false; }
        }

        /// <summary>CheckActivation's delayed step (:165-185) for a key. Part C: the spoken message goes as ONE rivalmono
        /// that carries the special activated / deactivated text native raises right after it (read: HasMessageBeenSent is
        /// already true there, :192/:201); the host flips the key's state HERE, directly - it never waits for a monologue.</summary>
        private static void ActivationStep(string key, SpecialRival rival, bool on)
        {
            try
            {
                var tl = rival.timeline;
                if (tl == null) return;
                string rid = rival.rivalData?.id ?? "";
                var st = Get(key, rid, true);
                bool flips;
                lock (_lock) flips = st.IsActive != on;
                List<RivalMonoTrailing> tr = null;
                if (flips)
                    tr = new List<RivalMonoTrailing> { new RivalMonoTrailing { Key = on ? "ba:messagetype_rivalry_activated" : "ba:messagetype_rivalry_deactivated", Read = true, Special = true } };
                if (DeliverOnce(key, rival, on ? tl.activationMessageKey : tl.deactivationMessageKey, on ? "activation" : "deactivation", tr, out int carried))
                    SetActive(key, rival, on, carried);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] step {(on ? "activation" : "deactivation")} for {key}: {ex.Message}"); }
        }

        /// <summary>ActivateRival / DeactivateRival (:187-203) for a key: flip, then the game's own silent special text -
        /// already carried by the rivalmono just sent (carriedTo &gt; 0), else sent alone, read = HasMessageBeenSent.</summary>
        private static void SetActive(string key, SpecialRival rival, bool on, int carriedTo = 0)
        {
            try
            {
                string rid = rival.rivalData?.id ?? "";
                var st = Get(key, rid, true);
                lock (_lock)
                {
                    if (st.IsActive == on) return;
                    st.IsActive = on;
                    // Part B (manager ruling 2026-09-27): planned entries are NOT dropped on deactivation - native keeps
                    // firing them (RivalTimeline.PlannedEntriesCoroutine checks only IsCompleted).
                }
                int n = carriedTo;
                if (n <= 0)
                {
                    var pids = OnlinePidsOfKey(key);
                    pids.Remove(MPConfig.PlayerId);
                    string spoken = on ? rival.timeline?.activationMessageKey : rival.timeline?.deactivationMessageKey;
                    bool rd;
                    lock (_lock) rd = !string.IsNullOrEmpty(spoken) && st.Sent.Contains(spoken);
                    n = CompanyMessages.SendRivalMonoToPids(rival, on ? "ba:messagetype_rivalry_activated" : "ba:messagetype_rivalry_deactivated", null, rd, true, null, pids);
                }
                Plugin.Logger.LogInfo($"[RivalAttn] {(on ? "ACTIVATED" : "deactivated")} rival {rid} for {key} (special text to {n} player(s)); the host's own state is untouched.");
                MPServer.PublishRivalStateIfChanged("rivalattn");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] set active {key}: {ex.Message}"); }
        }

        /// <summary>CheckSurrender (:134-147): the same income gate. The host runs DefeatRival ONCE, for everybody,
        /// after the native delay; the surrender text goes to every key that has the rival active.</summary>
        private static bool CheckSurrender(SpecialRival rival, SpecialRivalState nst)
        {
            var rd = rival.rivalData;
            if (rd.ownedRetailOfficeBusinesses != null && rd.ownedRetailOfficeBusinesses.Count > 0 && !(rd.WeeklyIncome < 2000f)) return false;
            if (nst != null && nst.isDefeated) return true;
            ScheduleSurrender(rd);
            return true;
        }

        /// <summary>The surrender step (host): after the native delay the host runs DefeatRival DIRECTLY (no monologue
        /// here - native waits for its own; the keys get theirs from the defeat postfix), and only if still undefeated.</summary>
        private static void ScheduleSurrender(RivalData rd)
        {
            string rid = rd.id ?? "";
            lock (_lock) _planned.RemoveAll(p => p.RivalId == rid);
            Schedule($"surrender|{rid}", () =>
            {
                try
                {
                    var ns = RivalsHelper.GetSpecialRivalState(rid);
                    if (ns != null && ns.isDefeated) { Plugin.Logger.LogInfo($"[RivalAttn] rival {rid} surrender step: already defeated - DefeatRival not run again."); return; }
                    Plugin.Logger.LogInfo($"[RivalAttn] rival {rid} surrenders (per-player path) - the host runs DefeatRival once; its postfix tells every key that had the rival active.");
                    RivalsHelper.DefeatRival(rd);
                    MPServer.PublishRivalStateIfChanged("rivalattn-surrender");
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] surrender {rid}: {ex.Message}"); }
            });
        }

        /// <summary>Part D F5 (HOST): the rival is defeated - by either path (the native host timeline or the per-player
        /// replay). EVERY key that had it active gets the surrender text (online members; nobody online = not sent)
        /// and goes inactive, whether or not a check reached that key in the sweep. Idempotent: a second call finds
        /// no active key.</summary>
        /// <summary>DEV (rivalattn mono): how many times DefeatRival actually defeated a special rival here.</summary>
        internal static int DefeatRuns;

        internal static void OnRivalDefeated(RivalData rd)
        {
            try
            {
                if (!MPServer.IsRunning || rd == null) return;
                string rid = rd.id ?? "";
                if (rid.Length == 0) return;
                var nst = RivalsHelper.GetSpecialRivalState(rid);
                if (nst == null || !nst.isDefeated) return;
                var sr = RivalsHelper.GetSpecialRival(rid);
                if (sr == null) return;
                var keys = new List<string>();
                lock (_lock)
                {
                    foreach (var kv in _store)
                        if (kv.Value.IsActive && kv.Key.EndsWith("|" + rid, System.StringComparison.Ordinal)) keys.Add(kv.Key.Substring(0, kv.Key.Length - rid.Length - 1));
                    _planned.RemoveAll(p => p.RivalId == rid);
                    _warQ.Remove(rid);   // part B: no war waits on a defeated rival
                }
                if (keys.Count == 0) return;
                var told = new List<string>();
                foreach (var k in keys)
                {
                    if (DeliverOnce(k, sr, sr.timeline?.surrenderMessageKey, "surrender")) told.Add(k);
                    var st = Get(k, rid, false);
                    if (st != null) lock (_lock) st.IsActive = false;
                }
                Plugin.Logger.LogInfo($"[RivalAttn] rival {rid} defeated: surrender rivalmono to {told.Count} of {keys.Count} key(s) that had it active [{string.Join(",", told)}]; all set inactive.");
                MPServer.PublishRivalStateIfChanged("rivalattn-defeat");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] defeat: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(RivalsHelper), "DefeatRival", new[] { typeof(RivalData) })]
        public static class Patch_DefeatRival_TellEveryKey
        {
            static void Prefix(RivalData rival, out bool __state)
            {
                __state = false;
                try { var s = rival != null ? RivalsHelper.GetSpecialRivalState(rival.id) : null; __state = s != null && !s.isDefeated; } catch { }
            }

            static void Postfix(RivalData rival, bool __state)
            {
                if (__state) DefeatRuns++;   // DEV count (rivalattn mono): real defeats on this machine
                if (!MPServer.IsRunning || rival == null) return;
                try { OnRivalDefeated(rival); }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] defeat postfix: {ex.Message}"); }
            }
        }

        /// <summary>CheckTriggers (:205-239) for a key: same thresholds, same planning times.</summary>
        private static void CheckTriggers(string key, SpecialRival rival, int businesses, float income)
        {
            var tl = rival.timeline;
            if (tl?.allEntries == null) return;
            string rid = rival.rivalData.id ?? "";
            float pct = income / rival.rivalData.WeeklyIncome * 100f;
            var st = Get(key, rid, true);
            foreach (var e in tl.allEntries)
            {
                if (e == null || string.IsNullOrEmpty(e.id)) continue;
                lock (_lock)
                {
                    if (st.Completed.Contains(e.id)) continue;
                    if (!(businesses >= e.businesses && pct >= (float)e.weeklyIncomePercentage)) continue;
                    bool planned = false;
                    foreach (var p in _planned) if (p.Key == key && p.RivalId == rid && p.EntryId == e.id) { planned = true; break; }
                    if (planned) continue;
                    int due = NativeDueMinute();
                    _planned.Add(new Planned { Key = key, RivalId = rid, EntryId = e.id, Mechanic = e.defense.ToString(), DueMin = due });
                    Plugin.Logger.LogInfo($"[RivalAttn] planned {e.defense} ({e.aggression}) entry {e.id} for {key} on {rid} at d{due / 1440} {due % 1440 / 60:00}:{due % 60:00} (businesses {businesses} >= {e.businesses}, income {pct:F1}% >= {e.weeklyIncomePercentage}%).");
                }
            }
        }

        private static int NowMinute()
        {
            var t = TimeHelper.Now();
            return t.Day * 1440 + t.Hour * 60 + (int)t.Minute;
        }

        /// <summary>The planning time CheckTriggers (:216-235) picks: before 8 -> today 8-10:xx; 8-16 -> now + 10-40 min;
        /// from 17 -> tomorrow 8-10:xx.</summary>
        private static int NativeDueMinute()
        {
            var t = TimeHelper.Now();
            int day = t.Day, hour = t.Hour;
            if (hour >= 8)
            {
                if (hour >= 17) return (day + 1) * 1440 + UnityEngine.Random.Range(8, 11) * 60 + UnityEngine.Random.Range(0, 59);
                return NowMinute() + UnityEngine.Random.Range(10, 40);
            }
            return day * 1440 + UnityEngine.Random.Range(8, 11) * 60 + UnityEngine.Random.Range(0, 59);
        }

        /// <summary>HOST heartbeat (3 s, main thread): run due delayed steps and due planned entries (PART A: DRY RUN).</summary>
        internal static void Tick()
        {
            if (!MPServer.IsRunning) return;
            try
            {
                if (!EnsureBound()) return;
                RefreshHostRowsCache();
                float now = UnityEngine.Time.realtimeSinceStartup;
                var run = new List<Pending>();
                lock (_lock)
                {
                    for (int i = _pending.Count - 1; i >= 0; i--)
                        if (_pending[i].Due <= now) { run.Add(_pending[i]); _pending.RemoveAt(i); }
                }
                foreach (var p in run)
                {
                    try { p.Run?.Invoke(); }
                    catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] step {p.Tag}: {ex.Message}"); }
                }
                int nowMin = NowMinute();
                var fire = new List<Planned>();
                lock (_lock)
                {
                    for (int i = _planned.Count - 1; i >= 0; i--)
                        if (_planned[i].DueMin <= nowMin) { fire.Add(_planned[i]); _planned.RemoveAt(i); }
                }
                foreach (var p in fire)
                {
                    var st = Get(p.Key, p.RivalId, false);
                    bool done;
                    // Part B (manager ruling 2026-09-27): mirror native - a planned entry fires even after the key was
                    // deactivated (PlannedEntriesCoroutine checks only IsCompleted); a defeat clears the plan.
                    lock (_lock) done = st == null || st.Completed.Contains(p.EntryId);
                    if (done) continue;
                    try
                    {
                        var rival = RivalsHelper.GetSpecialRival(p.RivalId);
                        var nst = RivalsHelper.GetSpecialRivalState(p.RivalId);
                        if (rival?.rivalData == null || nst == null || nst.isDefeated) continue;
                        TimelineEntry entry = null;
                        if (rival.timeline?.allEntries != null)
                            foreach (var e in rival.timeline.allEntries) if (e != null && e.id == p.EntryId) { entry = e; break; }
                        if (entry == null) continue;
                        FireFor(p.Key, rival, entry.defense, (int)entry.aggression, entry, false);
                    }
                    catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] error firing entry {p.EntryId} for {p.Key}: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] tick: {ex.Message}"); }
        }

        // ── per-peer R3 overlay (g) ─────────────────────────────────────────────────────────────
        /// <summary>A non-host key's own values over one native row: isActive, sent keys, completed ids.
        /// Defeat and running attacks stay the world's (native).</summary>
        internal static void OverlayFor(string key, CbRivalState row)
        {
            if (row == null) return;
            var st = Get(key, row.RivalId, false);
            lock (_lock)
            {
                row.IsActive = !row.IsDefeated && st != null && st.IsActive;
                row.SentKeys = st == null ? new List<string>() : new List<string>(st.Sent);
                row.CompletedIds = st == null ? new List<string>() : new List<string>(st.Completed);
            }
        }

        // ── rent block per player (g) - part D ──────────────────────────────────────────────────
        /// <summary>Is the rival active FOR THIS KEY? The host key reads the native state; any other key its own
        /// stored state; an empty key (a peer the host cannot key) is never active. Defeated = never active.</summary>
        internal static bool IsActiveFor(string rivalId, string key)
        {
            if (string.IsNullOrEmpty(rivalId) || string.IsNullOrEmpty(key)) return false;
            try
            {
                var nst = RivalsHelper.GetSpecialRivalState(rivalId);
                if (nst != null && nst.isDefeated) return false;
                if (key == HostKey) return nst != null && nst.isActive;
                var st = Get(key, rivalId, false);
                if (st == null) return false;
                lock (_lock) return st.IsActive;
            }
            catch { return false; }
        }

        /// <summary>HOST: the authority's copy of the game's rent / overtake rival gate (BizManPresentation.cs:538-551 /
        /// :751-764: the special rival that owns the BUILDING, active) - read for the REQUESTING player's key.
        /// Logs one line when the building belongs to a special rival. True = refuse.</summary>
        /// <summary>The RentDeny reason the host's rival gate sends; the client answers it with the game's own rival
        /// response (fold R4, MPClient.HandleRentDeny).</summary>
        internal const string RentDenyRivalReason = "owned by an active rival";

        internal static bool HostRefusesFor(BuildingRegistration reg, string addressKey, string pid, string via, bool skipPlayerOwned, out string rivalId)
        {
            rivalId = "";
            try
            {
                if (reg == null || !RivalsHelper.IsFeatureEnabled) return false;
                if (skipPlayerOwned && reg.BuildingOwnedByPlayer) return false;
                var rival = RivalsHelper.GetSpecialRival(reg.buildingOwnerRivalId);
                rivalId = rival?.rivalData?.id ?? "";
                if (rivalId.Length == 0) return false;
                // Fold R3: a building a PLAYER bought no longer belongs to the rival - the game clears the field on the
                // buyer's machine (BizManPresentation.cs:954) and has no rival gate on buying, but the host's copy keeps
                // the field and reg.BuildingOwnedByPlayer is the HOST's own deed only. The deed ledger decides: rent and
                // a buy-out inside a player-bought building are allowed.
                if (!string.IsNullOrEmpty(addressKey) && MPServer.BuildingRealEstateOwners.TryGetValue(addressKey, out var deed) && !string.IsNullOrEmpty(deed))
                {
                    Plugin.Logger.LogInfo($"[RivalSync] {via} of '{addressKey}' by '{pid}' allowed: the building is player-bought (deed '{deed}') - rival '{rivalId}' no longer owns it.");
                    rivalId = "";
                    return false;
                }
                string key = KeyOfPid(pid);
                bool refuse = IsActiveFor(rivalId, key);
                Plugin.Logger.LogInfo($"[RivalSync] {via} of '{addressKey}' by '{pid}' {(refuse ? "refused" : "allowed")} for {(key.Length > 0 ? key : "<no key>")}: the building's rival '{rivalId}' is {(refuse ? "active" : "not active")} for that key{(key.Length > 0 && key == HostKey ? " (the host key: native state)" : "")}.");
                return refuse;
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalSync] {via} rival gate for '{addressKey}': {ex.Message}"); return false; }
        }

        // ── merged companies (h) - key moves at merge / dissolve (part D F3) ────────────────────
        internal sealed class KeyMove
        {
            public Dictionary<string, string> Before = new Dictionary<string, string>(System.StringComparer.Ordinal);
            public string HostBefore = "";
        }

        /// <summary>HOST, BEFORE a merger store change: every named member's key now (store reads only, any thread).</summary>
        internal static KeyMove BeginMembershipChange(IEnumerable<string> stables)
        {
            var mv = new KeyMove();
            try
            {
                mv.HostBefore = HostKey;
                if (stables != null)
                    foreach (var s in stables)
                        if (!string.IsNullOrEmpty(s) && !mv.Before.ContainsKey(s)) mv.Before[s] = KeyOfStable(s);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] key move begin: {ex.Message}"); }
            return mv;
        }

        /// <summary>HOST, AFTER the store change: read the new keys now, move the state on the main thread.</summary>
        internal static void EndMembershipChange(KeyMove mv, string why)
        {
            try
            {
                if (mv == null || mv.Before.Count == 0 || !MPServer.IsRunning) return;
                var after = new Dictionary<string, string>(System.StringComparer.Ordinal);
                foreach (var s in mv.Before.Keys) after[s] = KeyOfStable(s);
                string hostAfter = HostKey;
                bool any = false;
                foreach (var kv in mv.Before) if (after[kv.Key] != kv.Value) { any = true; break; }
                if (!any) return;
                GameStatePatcher.EnqueueOnMainThread(() => ApplyKeyMove(mv, after, hostAfter, why));
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] key move end: {ex.Message}"); }
        }

        /// <summary>Fold R2: does a member other than the host hold this key after the change?</summary>
        private static bool HeldByOthers(string key, Dictionary<string, string> after)
        {
            if (string.IsNullOrEmpty(key) || after == null) return false;
            foreach (var kv in after)
                if (kv.Key != MPConfig.StableId && kv.Value == key) return true;
            return false;
        }

        private static State ReadKeyState(string key, string rid, string hostKeyThen)
        {
            if (key == hostKeyThen)
            {
                var nst = RivalsHelper.GetSpecialRivalState(rid);
                if (nst == null) return null;
                bool hcc; lock (_lock) hcc = _hostCopycat.Contains(rid);   // fold B4: the host key's copycat flag travels with its state
                return new State { IsActive = nst.isActive && !nst.isDefeated, EverCounted = EntranceIn(rid, nst.sentMessageKeys), CopycatDone = hcc,   // fold B1
                                   Completed = new List<string>(nst.completedTimelineEntryIds ?? new List<string>()),
                                   Sent = new List<string>(nst.sentMessageKeys ?? new List<string>()) };
            }
            var st = Get(key, rid, false);
            if (st == null) return null;
            lock (_lock) return new State { IsActive = st.IsActive, CopycatDone = st.CopycatDone, EverCounted = st.EverCounted,
                                            Completed = new List<string>(st.Completed), Sent = new List<string>(st.Sent) };
        }

        /// <summary>MERGE: every old key feeding one new key is joined into it (active if any was; completed and sent
        /// lists joined; planned entries and the DEV floor moved). DISSOLVE: each member's new key gets a copy of
        /// the company's state; the next check re-counts. The host key's state is the native one (written in place,
        /// add-only). A key nobody holds any more leaves the store.</summary>
        private static void ApplyKeyMove(KeyMove mv, Dictionary<string, string> after, string hostAfter, string why)
        {
            try
            {
                string hostBefore = mv.HostBefore;
                var sources = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);   // new key -> old keys
                var targetsOfOld = new Dictionary<string, HashSet<string>>(System.StringComparer.Ordinal);
                foreach (var kv in mv.Before)
                {
                    string oldK = kv.Value, newK = after[kv.Key];
                    if (oldK == newK || oldK.Length == 0 || newK.Length == 0) continue;
                    if (!sources.TryGetValue(newK, out var l)) { l = new List<string>(); sources[newK] = l; }
                    if (!l.Contains(oldK)) l.Add(oldK);
                    if (!targetsOfOld.TryGetValue(oldK, out var ts)) { ts = new HashSet<string>(System.StringComparer.Ordinal); targetsOfOld[oldK] = ts; }
                    ts.Add(newK);
                }
                if (sources.Count == 0) return;
                var stillUsed = new HashSet<string>(after.Values, System.StringComparer.Ordinal) { hostAfter };
                var rivals = new List<string>();
                foreach (var r in RivalsHelper.GetSpecialRivals()) { string id = r?.rivalData?.id ?? ""; if (id.Length > 0 && !rivals.Contains(id)) rivals.Add(id); }
                bool hostMoved = hostBefore.Length > 0 && hostAfter.Length > 0 && hostBefore != hostAfter;

                // Fold R2: the HOST left a company that carries on - the company key (the native state until now) becomes an
                // ordinary key for the members who stay: copy the native state into it, as a dissolve does. Read BEFORE
                // anything below writes the native state.
                if (hostMoved && HeldByOthers(hostBefore, after))
                {
                    int cp = 0;
                    var actCp = new List<string>();
                    foreach (var rid in rivals)
                    {
                        var s = ReadKeyState(hostBefore, rid, hostBefore);
                        if (s == null) continue;
                        var nstCp = RivalsHelper.GetSpecialRivalState(rid);
                        bool defCp = nstCp != null && nstCp.isDefeated;
                        var st = Get(hostBefore, rid, true);
                        lock (_lock)
                        {
                            st.IsActive |= s.IsActive; st.EverCounted |= s.EverCounted; st.CopycatDone |= s.CopycatDone;   // folds B1 / B4
                            foreach (var c in s.Completed) if (!st.Completed.Contains(c)) st.Completed.Add(c);
                            foreach (var x in s.Sent) if (!st.Sent.Contains(x)) st.Sent.Add(x);
                            if (defCp) st.IsActive = false;
                            if (st.IsActive) actCp.Add(rid);
                        }
                        cp++;
                    }
                    Plugin.Logger.LogInfo($"[RivalAttn] host left ({why}): {hostBefore} carries on without the host - the native state copied into it: {cp} rival row(s), active on [{string.Join(",", actCp)}].");
                }

                // The new host key last: every other target reads the old host key's native state unchanged.
                var targets = new List<KeyValuePair<string, List<string>>>(sources);
                targets.Sort((x, y) => (x.Key == hostAfter ? 1 : 0) - (y.Key == hostAfter ? 1 : 0));
                foreach (var tgt in targets)
                {
                    int rows = 0;
                    var activeOn = new List<string>();
                    // Fold R1: the host JOINED an existing company (GROWN) or its company was absorbed (UNION) - the
                    // company's stored rows are folded into the native state too, then leave the store.
                    bool foldTarget = hostMoved && tgt.Key == hostAfter;
                    foreach (var rid in rivals)
                    {
                        var parts = new List<State>();
                        foreach (var oldK in tgt.Value) { var s = ReadKeyState(oldK, rid, hostBefore); if (s != null) parts.Add(s); }
                        if (foldTarget) { var ts = ReadKeyState(tgt.Key, rid, hostBefore); if (ts != null) parts.Add(ts); }
                        if (parts.Count == 0) continue;
                        var nst = RivalsHelper.GetSpecialRivalState(rid);
                        bool defeated = nst != null && nst.isDefeated;
                        if (tgt.Key == hostAfter)
                        {
                            if (nst == null) continue;
                            bool act = nst.isActive;
                            if (nst.completedTimelineEntryIds == null) nst.completedTimelineEntryIds = new List<string>();
                            if (nst.sentMessageKeys == null) nst.sentMessageKeys = new List<string>();
                            foreach (var p in parts)
                            {
                                act |= p.IsActive;
                                if (p.CopycatDone) lock (_lock) _hostCopycat.Add(rid);   // fold B4: a merge carries the copycat flag into the host key
                                foreach (var c in p.Completed) if (!nst.completedTimelineEntryIds.Contains(c)) nst.completedTimelineEntryIds.Add(c);
                                foreach (var s in p.Sent) if (!nst.sentMessageKeys.Contains(s)) nst.sentMessageKeys.Add(s);   // in place: the game caches the list
                            }
                            if (!defeated) nst.isActive = act;
                            if (nst.isActive) activeOn.Add(rid);
                        }
                        else
                        {
                            var st = Get(tgt.Key, rid, true);
                            lock (_lock)
                            {
                                foreach (var p in parts)
                                {
                                    st.IsActive |= p.IsActive; st.CopycatDone |= p.CopycatDone; st.EverCounted |= p.EverCounted;
                                    foreach (var c in p.Completed) if (!st.Completed.Contains(c)) st.Completed.Add(c);
                                    foreach (var s in p.Sent) if (!st.Sent.Contains(s)) st.Sent.Add(s);
                                }
                                if (defeated) st.IsActive = false;
                                if (st.IsActive) activeOn.Add(rid);
                            }
                        }
                        rows++;
                    }
                    string kind = tgt.Value.Count > 1 || tgt.Key.StartsWith("g:", System.StringComparison.Ordinal) ? "merge" : "dissolve";
                    Plugin.Logger.LogInfo($"[RivalAttn] {kind} ({why}): {string.Join(" + ", tgt.Value)} -> {tgt.Key}{(tgt.Key == hostAfter ? " (host key: native state)" : "")}: {rows} rival row(s) combined, active on [{string.Join(",", activeOn)}].");
                    if (foldTarget)
                    {
                        int dropped = 0, plannedDropped = 0;
                        lock (_lock)
                        {
                            foreach (var sk in new List<string>(_store.Keys))
                                if (sk.StartsWith(tgt.Key + "|", System.StringComparison.Ordinal)) { _store.Remove(sk); dropped++; }
                            plannedDropped = _planned.RemoveAll(p => p.Key == tgt.Key);   // the native timeline plans for the host key itself
                            _pending.RemoveAll(p => p.Tag != null && p.Tag.StartsWith(tgt.Key + "|", System.StringComparison.Ordinal));
                        }
                        Plugin.Logger.LogInfo($"[RivalAttn] host key moved ({why}): {hostBefore} -> {tgt.Key}: the company's stored rows folded into the native state and removed ({dropped} row(s), {plannedDropped} planned entr(ies) dropped).");
                    }
                }

                // Keys nobody holds any more: planned entries / DEV floor follow a single successor; the rows leave the store.
                lock (_lock)
                    foreach (var o in targetsOfOld)
                    {
                        string oldK = o.Key;
                        if (stillUsed.Contains(oldK)) continue;
                        string heir = o.Value.Count == 1 ? new List<string>(o.Value)[0] : "";
                        if (heir.Length > 0)   // part B: attacks and queue places move to the key that inherits (the host key too)
                        {
                            foreach (var a in _attacks) if (a.Key == oldK) a.Key = heir;
                            foreach (var q in _warQ.Values) foreach (var x in q) if (x.Key == oldK) x.Key = heir;
                        }
                        // Not folded (manager 2026-09-27): a company SPLIT leaves the old key with no single heir, so its
                        // queue places are dropped here - the split partners queue again from the back.
                        else
                            foreach (var q in _warQ.Values) q.RemoveAll(x => x.Key == oldK);   // fold B2: nobody holds the key any more
                        if (heir == hostAfter) heir = "";   // the native timeline plans for the host key itself
                        foreach (var p in _planned)
                            if (p.Key == oldK) p.Key = heir;
                        _planned.RemoveAll(p => string.IsNullOrEmpty(p.Key));
                        var seenPlan = new HashSet<string>(System.StringComparer.Ordinal);
                        _planned.RemoveAll(p => !seenPlan.Add(p.Key + "|" + p.RivalId + "|" + p.EntryId));   // one per key+entry after the move
                        _pending.RemoveAll(p => p.Tag != null && p.Tag.StartsWith(oldK + "|", System.StringComparison.Ordinal));
                        foreach (var ek in new List<string>(_extra.Keys))
                            if (ek.StartsWith(oldK + "|", System.StringComparison.Ordinal))
                            {
                                int v = _extra[ek]; _extra.Remove(ek);
                                if (heir.Length > 0) { string nk = heir + ek.Substring(oldK.Length); _extra.TryGetValue(nk, out var have); _extra[nk] = System.Math.Max(have, v); }
                            }
                        foreach (var sk in new List<string>(_store.Keys))
                            if (sk.StartsWith(oldK + "|", System.StringComparison.Ordinal)) _store.Remove(sk);
                    }
                lock (_lock)
                    foreach (var q in _warQ.Values)
                    {
                        // fold B2: one place per key after the renames - the earliest POSITION kept; fold E6: with the LATEST retry time
                        var latestTry = new Dictionary<string, int>(System.StringComparer.Ordinal);
                        foreach (var x in q) if (!latestTry.TryGetValue(x.Key, out int lt) || x.LastTryMin > lt) latestTry[x.Key] = x.LastTryMin;
                        var seenQ = new HashSet<string>(System.StringComparer.Ordinal);
                        q.RemoveAll(x => !seenQ.Add(x.Key));
                        foreach (var x in q) x.LastTryMin = latestTry[x.Key];
                    }
                MPServer.PublishRivalStateIfChanged("rivalattn-" + why);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] key move ({why}): {ex.GetType().Name}: {ex.Message}"); }
        }

        // ── merged host: co-members' qualifying shops join the native count (h) ──────────────────
        /// <summary>Replaces the pooled Patch_RivalSeesAllPlayers: on the host, a MERGED host key adds only its
        /// co-members' qualifying shops (their own machines' native test) to the game's GetPlayerValues. A host
        /// that is not merged gets the game's value untouched.</summary>
        [HarmonyPatch(typeof(RivalTimeline), "GetPlayerValues")]
        public static class Patch_MergedHostCoMembers
        {
            static void Postfix(SpecialRival rival, ref (List<BuildingRegistration>, float) __result)
            {
                if (!MPServer.IsRunning || rival == null) return;
                try
                {
                    string hk = HostKey;
                    if (!hk.StartsWith("g:", System.StringComparison.Ordinal)) return;
                    var list = __result.Item1 ?? new List<BuildingRegistration>();
                    float income = __result.Item2;
                    string nb = rival.primaryNeighborhood ?? "";
                    int added = 0;
                    foreach (var stable in StablesOfKey(hk))
                    {
                        if (stable == MPConfig.StableId) continue;
                        foreach (var pid in CountingPidsOfStable(stable))   // fold R6
                        {
                            foreach (var row in MPServer.SelfReportRows(pid))
                            {
                                if (row == null || !row.RivalQualifying || row.Neighborhood != nb) continue;
                                var reg = GameStatePatcher.FindRegistration(row.AddressKey ?? "");
                                if (reg == null || list.Contains(reg)) continue;
                                list.Add(reg);
                                income += row.WeeklyIncome;
                                added++;
                            }
                        }
                    }
                    if (added > 0) __result = (list, income);
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] merged host count: {ex.Message}"); }
            }
        }

        /// <summary>The sweep postfix: after RivalsHelper.CheckRivalTimelines and CheckRivalTimeline(nb).</summary>
        [HarmonyPatch]
        public static class Patch_AfterTimelineSweep
        {
            static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var found = new List<System.Reflection.MethodBase>();
                try
                {
                    foreach (var m in typeof(RivalsHelper).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                                                    | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                        if (m.Name == "CheckRivalTimelines" || m.Name == "CheckRivalTimeline") found.Add(m);
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] sweep targets: {ex.Message}"); }
                return found;
            }

            static void Postfix(System.Reflection.MethodBase __originalMethod, object[] __args)
            {
                try
                {
                    if (!MPServer.IsRunning) return;
                    string nb = null;
                    if (__originalMethod?.Name == "CheckRivalTimeline" && __args != null && __args.Length > 0) nb = __args[0] as string;
                    AfterNativeSweep(string.IsNullOrEmpty(nb) ? null : nb);
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] sweep postfix: {ex.Message}"); }
            }
        }

        // ── part B: attacks aimed at one key, the price-war queue, the restore check, copycat waves ──────────────
        // design_rivals.md (c)(d)(e). HOST only (the attack calls and the defense clock run on the host alone).

        /// <summary>The key the running ActivatePriceReduction / ActivateLowDemand call is aimed at. Null = the HOST key
        /// (the game's own timeline, or a native console call). Set only around the mod path's own calls (FireFor).</summary>
        internal static string Target;
        private static string _targetEntry;
        private static bool _forcing;          // rivalforce on the host key: no silent completion of a native entry

        internal sealed class WarCtx
        {
            public string Key, Rid, Nb;
            public int Aggr, Before;
            public Dictionary<string, float> Pre = new Dictionary<string, float>(System.StringComparer.Ordinal);
        }

        private sealed class WaveCtx
        {
            public string Key, Rid, Nb;
            public int Aggr, CapLeft = -1, Before;
            public bool Gate;
            public List<string> Opened = new List<string>();
        }
        private static WaveCtx _wave;

        private static string AttackerKey => string.IsNullOrEmpty(Target) ? HostKey : Target;

        private static int EndMinOf(DefenseState ds)
        {
            try { return ds == null ? 0 : (int)System.Math.Round(ds.timestamp.GetTotalMinutes()); } catch { return 0; }
        }

        private static int NowTotalMin()
        {
            try { return (int)System.Math.Round(TimeHelper.NowInMinutes()); } catch { return 0; }
        }

        private static string FmtMin(int m) => m <= 0 ? "-" : $"d{m / 1440} {m % 1440 / 60:00}:{m % 60:00}";

        private static string F2(float v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        // ── world data (persisted as "*world" rows) ──
        private static void ClearWorldLocked()
        {
            _attacks.Clear(); _warQ.Clear(); _copyAdded.Clear(); _hostCopycat.Clear();
            _wave = null; Target = null; _targetEntry = null; _forcing = false;
        }

        private static int QueuedCountLocked()
        {
            int n = 0;
            foreach (var q in _warQ.Values) n += q.Count;
            return n;
        }

        private static MpRivalAttack CopyAttack(MpRivalAttack a)
        {
            var c = new MpRivalAttack { Key = a.Key, RivalId = a.RivalId, Nb = a.Nb, Mechanic = a.Mechanic, Aggression = a.Aggression,
                                        EntryId = a.EntryId, EndMin = a.EndMin, Cut = a.Cut, Opened = new List<string>(a.Opened ?? new List<string>()) };
            if (a.Items != null) foreach (var it in a.Items) if (it != null) c.Items.Add(new MpRivalWarItem { Addr = it.Addr, Item = it.Item, Pre = it.Pre });
            return c;
        }

        private static void ApplyWorldRowLocked(MpRivalAttnEntry e)
        {
            string rid = e.RivalId;
            if (e.Attacks != null) foreach (var a in e.Attacks) if (a != null && !string.IsNullOrEmpty(a.Key)) { var c = CopyAttack(a); c.RivalId = rid; _attacks.Add(c); }
            if (e.Queue != null && e.Queue.Count > 0)
            {
                var q = new List<MpRivalQueued>();
                foreach (var x in e.Queue) if (x != null && !string.IsNullOrEmpty(x.Key)) q.Add(new MpRivalQueued { Key = x.Key, EntryId = x.EntryId, Aggression = x.Aggression, AtMin = x.AtMin, LastTryMin = x.LastTryMin });
                if (q.Count > 0) _warQ[rid] = q;
            }
            if (e.CopycatAdded > 0) _copyAdded[rid] = e.CopycatAdded;
        }

        private static List<MpRivalAttnEntry> WorldRowsLocked()
        {
            var rids = new List<string>();
            foreach (var a in _attacks) if (!rids.Contains(a.RivalId)) rids.Add(a.RivalId);
            foreach (var kv in _warQ) if (kv.Value.Count > 0 && !rids.Contains(kv.Key)) rids.Add(kv.Key);
            foreach (var kv in _copyAdded) if (kv.Value > 0 && !rids.Contains(kv.Key)) rids.Add(kv.Key);
            var rows = new List<MpRivalAttnEntry>();
            foreach (var rid in rids)
            {
                var row = new MpRivalAttnEntry { Key = WorldKey, RivalId = rid, Attacks = new List<MpRivalAttack>(), Queue = new List<MpRivalQueued>() };
                foreach (var a in _attacks) if (a.RivalId == rid) row.Attacks.Add(CopyAttack(a));
                if (_warQ.TryGetValue(rid, out var q))
                    foreach (var x in q) row.Queue.Add(new MpRivalQueued { Key = x.Key, EntryId = x.EntryId, Aggression = x.Aggression, AtMin = x.AtMin, LastTryMin = x.LastTryMin });
                _copyAdded.TryGetValue(rid, out int added);
                row.CopycatAdded = added;
                rows.Add(row);
            }
            return rows;
        }

        // ── ownership, gate, copycat flag ──
        /// <summary>Whose attack is this native state? The recorded attack with the same rival, mechanic and end. An
        /// unrecorded state (started before part B, or by a native console call) is the HOST key's (migration rule).</summary>
        private static string OwnerOfState(string rid, DefenseState ds)
        {
            if (ds == null) return HostKey;
            int end = EndMinOf(ds);
            string mech = ds.defensiveMechanic.ToString();
            lock (_lock)
                foreach (var a in _attacks)
                    if (a.RivalId == rid && a.Mechanic == mech && a.EndMin == end) return a.Key;
            return HostKey;
        }

        private static bool StateAlive(MpRivalAttack a)
        {
            var nst = RivalsHelper.GetSpecialRivalState(a.RivalId);
            if (nst?.defenseStates == null) return false;
            foreach (var ds in nst.defenseStates)
                if (ds != null && ds.defensiveMechanic.ToString() == a.Mechanic && EndMinOf(ds) == a.EndMin) return true;
            return false;
        }

        /// <summary>Manager ruling 2026-09-27: the once-per-key and cap rules (and the wave spacing) bind only when at
        /// least 2 keys have EVER counted for the rival. The host key counts once the rival's entrance message was sent
        /// to it (native sent keys); every other key by its EverCounted flag. A solo host: gate off = native.</summary>
        internal static bool GateOn(string rid, out int keys)
        {
            keys = 0;
            try
            {
                var nst = RivalsHelper.GetSpecialRivalState(rid);
                var sr = RivalsHelper.GetSpecialRival(rid);
                if (nst?.sentMessageKeys != null && sr != null && !string.IsNullOrEmpty(sr.entranceMessageKey) && nst.sentMessageKeys.Contains(sr.entranceMessageKey)) keys++;
                string hk = HostKey;
                lock (_lock)
                    foreach (var kv in _store)
                        if (kv.Value.EverCounted && kv.Key.EndsWith("|" + rid, System.StringComparison.Ordinal) && !kv.Key.StartsWith(hk + "|", System.StringComparison.Ordinal)) keys++;
            }
            catch { }
            return keys >= 2;
        }

        /// <summary>Fold B1: a row derived from the HOST key's native state has counted for the rival exactly when GateOn's
        /// host test holds - the rival's entrance message is among its sent keys.</summary>
        private static bool EntranceIn(string rid, List<string> sent)
        {
            try
            {
                if (sent == null || string.IsNullOrEmpty(rid)) return false;
                var sr = RivalsHelper.GetSpecialRival(rid);
                return sr != null && !string.IsNullOrEmpty(sr.entranceMessageKey) && sent.Contains(sr.entranceMessageKey);
            }
            catch { return false; }
        }

        /// <summary>Fold B2: does anybody still hold this key (a member whose key it is now)?</summary>
        private static bool KeyHeld(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key == HostKey) return true;
            try { foreach (var s in StablesOfKey(key)) if (KeyOfStable(s) == key) return true; } catch { }
            return false;
        }

        private static bool CopycatDoneFor(string key, string rid)
        {
            if (key == HostKey) { lock (_lock) return _hostCopycat.Contains(rid); }
            var st = Get(key, rid, false);
            if (st == null) return false;
            lock (_lock) return st.CopycatDone;
        }

        private static void SetCopycatDone(string key, string rid)
        {
            if (key == HostKey) { lock (_lock) _hostCopycat.Add(rid); return; }
            var st = Get(key, rid, true);
            lock (_lock) st.CopycatDone = true;
        }

        // ── (c) the aimed key's own shops / best sellers ──
        /// <summary>The key's own shops in a neighbourhood (headquarters excluded) with the daily income native ranks by
        /// (GetAvgDailyIncome(7)): the host's own from its registration, every other member's from its self-report row
        /// (AvgDaily7). Ownership is the ledger / stamp - RentedByPlayer alone may have been raised for every player.</summary>
        private static List<(BuildingRegistration reg, float daily)> KeyShopsIn(string key, string nb)
        {
            var list = new List<(BuildingRegistration reg, float daily)>();
            var gi = SaveGameManager.Current;
            if (gi?.BuildingRegistrations == null || string.IsNullOrEmpty(key)) return list;
            var stables = StablesOfKey(key);
            bool hostIn = stables.Contains(MPConfig.StableId);
            var pids = new HashSet<string>(System.StringComparer.Ordinal);
            var daily = new Dictionary<string, float>(System.StringComparer.Ordinal);
            foreach (var s in stables)
            {
                if (s == MPConfig.StableId) continue;
                foreach (var pid in PidsOfStable(s)) pids.Add(pid);
                foreach (var pid in CountingPidsOfStable(s))
                    foreach (var row in MPServer.SelfReportRows(pid))
                        if (row != null && row.Neighborhood == nb && !string.IsNullOrEmpty(row.AddressKey)) daily[row.AddressKey] = row.AvgDaily7;
            }
            foreach (var reg in gi.BuildingRegistrations)
            {
                if (reg == null) continue;
                try
                {
                    if (reg.Neighborhood != nb || reg.businessTypeName == "ba:businesstype_headquarters") continue;
                    bool stamped = GameStatePatcher.IsAnyPlayerBusiness(reg);
                    if (!reg.RentedByPlayer && !stamped) continue;
                    string owner = MPRivalFairness.Patch_CompetitionSeesAllPlayers.OwnerPidOf(reg);
                    if (owner.Length == 0 || owner == MPConfig.PlayerId)
                    {
                        if (hostIn && reg.RentedByPlayer) list.Add((reg, reg.GetAvgDailyIncome(7)));
                    }
                    else if (pids.Contains(owner))
                    {
                        string addr = GameStateReader.AddressKey(reg);
                        if (!daily.TryGetValue(addr, out float d)) d = MPServer.SessionBusinessWeeklyIncome(addr) / 7f;
                        list.Add((reg, d));
                    }
                }
                catch { }
            }
            list.Sort((a, b) => b.daily.CompareTo(a.daily));
            return list;
        }

        /// <summary>GetTopIncomeBusinesses for the aimed key: only that key's shops, highest daily income first. Null =
        /// keep native's list (the host key, not merged, outside the tenancy raise - native is the host's own already).</summary>
        internal static List<BuildingRegistration> TopIncomeFor(int topNumber, string nb, bool underRaise)
        {
            string key = AttackerKey;
            if (key == HostKey && !key.StartsWith("g:", System.StringComparison.Ordinal) && !underRaise) return null;
            var outList = new List<BuildingRegistration>();
            foreach (var r in KeyShopsIn(key, nb)) { if (outList.Count >= topNumber) break; outList.Add(r.reg); }
            return outList;
        }

        /// <summary>GetTopSellingProducts for the aimed key (RivalDefenseHelper.cs:214-227): the key's items sold in the
        /// last 7 days (the host's own order history; every other member's self-report Sold7d), only items the rival
        /// sells in the neighbourhood, most sold first, retail products only, topNumber. Null = native (the host key,
        /// not merged: native already reads the host's own sales).</summary>
        internal static List<string> TopSellingFor(int topNumber, string nb)
        {
            string key = AttackerKey;
            if (key == HostKey && !key.StartsWith("g:", System.StringComparison.Ordinal)) return null;
            var rival = RivalsHelper.GetSpecialRivalByNeighborhood(nb);
            var gi = SaveGameManager.Current;
            if (rival?.rivalData == null || gi?.BuildingRegistrations == null) return null;
            string rid = rival.rivalData.id;
            var rivalSells = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var reg in gi.BuildingRegistrations)
                if (reg != null && reg.Neighborhood == nb && reg.businessOwnerRivalId == rid)
                    try { foreach (var it in reg.GetListOfItemsForSale()) if (!string.IsNullOrEmpty(it)) rivalSells.Add(it); } catch { }
            var sums = new Dictionary<string, int>(System.StringComparer.Ordinal);
            var order = new List<string>();
            void Add(string item, int n)
            {
                if (n <= 0 || string.IsNullOrEmpty(item) || !rivalSells.Contains(item)) return;
                if (!sums.ContainsKey(item)) { sums[item] = 0; order.Add(item); }
                sums[item] += n;
            }
            var stables = StablesOfKey(key);
            int day = gi.Day;
            if (stables.Contains(MPConfig.StableId))
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null || !reg.RentedByPlayer || reg.Neighborhood != nb || reg.orderHistory == null) continue;
                    string owner = MPRivalFairness.Patch_CompetitionSeesAllPlayers.OwnerPidOf(reg);
                    if (owner.Length > 0 && owner != MPConfig.PlayerId) continue;
                    foreach (var o in reg.orderHistory)
                    {
                        if (o == null || o.itemSales == null || o.dayNumber < day - 7 || o.dayNumber > day) continue;
                        foreach (var s in o.itemSales) if (s != null) Add(s.itemName, s.amountSold);
                    }
                }
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var s in stables)
            {
                if (s == MPConfig.StableId) continue;
                foreach (var pid in CountingPidsOfStable(s))
                    foreach (var row in MPServer.SelfReportRows(pid))
                    {
                        if (row == null || row.Neighborhood != nb || row.Sold7d == null || !seen.Add(row.AddressKey ?? "")) continue;
                        foreach (var kv in row.Sold7d) Add(kv.Key, kv.Value);
                    }
            }
            var result = new List<string>();
            foreach (var item in order.OrderByDescending(i => sums[i]))
            {
                if (result.Count >= topNumber) break;
                try
                {
                    var it = BigAmbitions.Items.ItemsGetter.GetByName(item);
                    if (it == null || (it.type & BigAmbitions.Items.ItemType.RetailProduct) == 0) continue;
                }
                catch { continue; }
                result.Add(item);
            }
            return result;
        }

        // ── (d) the price-war queue ──
        /// <summary>Prefix body on ActivatePriceReduction (host). A running war for this rival that belongs to a
        /// DIFFERENT key (or one ended but not yet through its restore check), or a queue whose head is another key =
        /// refuse and queue (first in, first out). The host's own path waits by itself: the game re-plans hourly.
        /// Otherwise the key leaves the queue and the rival's prices are noted for the restore check. True = run the game's own call.</summary>
        internal static bool WarGate(string nb, int aggr, ref bool result, out WarCtx ctx)
        {
            ctx = null;
            var rival = RivalsHelper.GetSpecialRivalByNeighborhood(nb);
            if (rival?.rivalData == null) return true;
            string rid = rival.rivalData.id ?? "";
            string me = AttackerKey;
            var nst = RivalsHelper.GetSpecialRivalState(rid);
            string blocker = "", why = "";
            if (nst?.defenseStates != null)
                foreach (var ds in nst.defenseStates)
                {
                    if (ds == null || ds.defensiveMechanic != DefensiveMechanic.PriceReduction) continue;
                    string o = OwnerOfState(rid, ds);
                    if (o != me) { blocker = o; why = $"its war runs until {FmtMin(EndMinOf(ds))}"; break; }
                }
            int now = NowTotalMin();
            int pos = 0, len = 0;
            // Fold B2: the queued keys' standing, read outside the lock (store and session reads).
            var unheld = new HashSet<string>(System.StringComparer.Ordinal);
            var offline = new HashSet<string>(System.StringComparer.Ordinal);
            var qKeys = new List<string>();
            lock (_lock) if (_warQ.TryGetValue(rid, out var q0)) foreach (var x in q0) if (x.Key != me && !qKeys.Contains(x.Key)) qKeys.Add(x.Key);
            foreach (var k in qKeys)
            {
                if (!KeyHeld(k)) unheld.Add(k);
                else if (OnlinePidsOfKey(k).Count == 0) offline.Add(k);
            }
            var skipped = new List<string>();
            lock (_lock)
            {
                if (blocker.Length == 0)
                    foreach (var a in _attacks)
                        if (a.RivalId == rid && a.Mechanic == "PriceReduction" && a.Key != me) { blocker = a.Key; why = "its war awaits the restore check"; break; }
                if (!_warQ.TryGetValue(rid, out var q)) { q = new List<MpRivalQueued>(); _warQ[rid] = q; }
                if (unheld.Count > 0)
                {
                    int gone = q.RemoveAll(x => unheld.Contains(x.Key));
                    if (gone > 0) Plugin.Logger.LogInfo($"[RivalWar] {gone} queued war(s) on {rid} dropped: nobody holds [{string.Join(",", unheld)}] any more.");
                }
                // Fold B2: rows ahead of this key - a key with no online member is skipped (it keeps its place, the next row
                // may start); an online row not retried for 48 game hours leaves; the first other online row blocks.
                int qi = 0;
                while (qi < q.Count)
                {
                    var x = q[qi];
                    if (x.Key == me) break;
                    // fold E2: a skipped offline row is stamped now - only ONLINE time counts toward the 48 game-hour idle drop
                    if (offline.Contains(x.Key)) { x.LastTryMin = now; if (!skipped.Contains(x.Key)) skipped.Add(x.Key); qi++; continue; }
                    if (now - x.LastTryMin > StaleQueueMin)
                    {
                        Plugin.Logger.LogInfo($"[RivalWar] queued war for {x.Key} on {rid} dropped from the queue: not retried for {(now - x.LastTryMin) / 60} game hour(s).");
                        q.RemoveAt(qi);
                        continue;
                    }
                    if (blocker.Length == 0) { blocker = x.Key; why = "it is ahead in the queue"; }
                    break;
                }
                if (blocker.Length > 0)
                {
                    int idx = q.FindIndex(x => x.Key == me);
                    if (idx < 0) { q.Add(new MpRivalQueued { Key = me, EntryId = _targetEntry ?? "", Aggression = aggr, AtMin = now, LastTryMin = now }); idx = q.Count - 1; }
                    else q[idx].LastTryMin = now;
                    pos = idx + 1; len = q.Count;
                }
                else
                {
                    // Its turn: the attempt leaves the queue whatever the game's own call then does (a war with
                    // nothing to cut ends the wait too - a later plan queues again behind whoever runs then).
                    int mi = q.FindIndex(x => x.Key == me);
                    if (mi >= 0) q.RemoveAt(mi);
                }
            }
            if (skipped.Count > 0)
                Plugin.Logger.LogInfo($"[RivalWar] queued war(s) for [{string.Join(",", skipped)}] on {rid} skipped for {me}: no online member (they keep their place).");
            if (blocker.Length > 0)
            {
                Plugin.Logger.LogInfo($"[RivalWar] war for {me} on {rid} WAITS (queue position {pos} of {len}): a war for {blocker} - {why}. Nothing cut.");
                result = false;
                return false;
            }
            ctx = new WarCtx { Key = me, Rid = rid, Nb = nb, Aggr = aggr, Before = nst?.defenseStates?.Count ?? 0 };
            foreach (var reg in SaveGameManager.Current.BuildingRegistrations)
            {
                if (reg == null || reg.businessOwnerRivalId != rid || reg.retailPrices == null) continue;
                string addr = GameStateReader.AddressKey(reg);
                foreach (var rp in reg.retailPrices) if (rp != null && !string.IsNullOrEmpty(rp.itemName)) ctx.Pre[addr + "|" + rp.itemName] = rp.price;
            }
            return true;
        }

        /// <summary>Postfix body on ActivatePriceReduction: a war that started is recorded (key, entry, end, the pre-cut
        /// price of every row it cut) and its key leaves the queue.</summary>
        internal static void AfterWar(WarCtx ctx, bool ok)
        {
            if (ctx == null || !ok) return;
            var list = RivalsHelper.GetSpecialRivalState(ctx.Rid)?.defenseStates;
            if (list == null || list.Count <= ctx.Before) return;
            var ds = list[list.Count - 1];
            if (ds == null || ds.defensiveMechanic != DefensiveMechanic.PriceReduction) return;
            float f = 1f;
            try { f = RivalDefenseHelper.GetPriceReductionValues((Enums.Priority)ctx.Aggr).Item1; } catch { }
            var atk = new MpRivalAttack { Key = ctx.Key, RivalId = ctx.Rid, Nb = ctx.Nb, Mechanic = "PriceReduction", Aggression = ctx.Aggr,
                                          EntryId = _targetEntry ?? "", EndMin = EndMinOf(ds), Cut = 1f - f };
            var affected = ds.affectedItems ?? new List<string>();
            double sumPre = 0, sumNow = 0;
            foreach (var reg in SaveGameManager.Current.BuildingRegistrations)
            {
                if (reg == null || reg.businessOwnerRivalId != ctx.Rid || reg.retailPrices == null) continue;
                string addr = GameStateReader.AddressKey(reg);
                foreach (var rp in reg.retailPrices)
                {
                    if (rp == null || !affected.Contains(rp.itemName)) continue;
                    if (!ctx.Pre.TryGetValue(addr + "|" + rp.itemName, out float pre)) continue;
                    atk.Items.Add(new MpRivalWarItem { Addr = addr, Item = rp.itemName, Pre = pre });
                    sumPre += pre; sumNow += rp.price;
                }
            }
            int queued;
            lock (_lock)
            {
                _attacks.Add(atk);
                if (_warQ.TryGetValue(ctx.Rid, out var q)) { int i = q.FindIndex(x => x.Key == ctx.Key); if (i >= 0) q.RemoveAt(i); }
                queued = _warQ.TryGetValue(ctx.Rid, out var q2) ? q2.Count : 0;
            }
            int n = atk.Items.Count;
            Plugin.Logger.LogInfo($"[RivalWar] war for {ctx.Key} on {ctx.Rid} STARTED: items [{string.Join(",", affected)}] on {n} price row(s), cut {atk.Cut * 100f:F0}% until {FmtMin(atk.EndMin)} (end {atk.EndMin}); avg price {(n > 0 ? sumPre / n : 0):F2} -> {(n > 0 ? sumNow / n : 0):F2}; entry {(atk.EntryId.Length > 0 ? atk.EntryId : "native")}; {queued} war(s) still queued.");
        }

        /// <summary>Postfix body on RivalDefenseHelper.RunHourly (host): every recorded attack whose native state the
        /// game just ended. A war: the game re-priced the rival's shops (HandleDefenseStateEnd); each recorded item is
        /// compared with its pre-war price - still at or below snapshot x (1 - cut) + 2% = written back to the snapshot
        /// (a warning; with the manager's gate on, or for a war aimed at a key other than the host's - fold B3; a solo host's
        /// own war stays native). The record leaves, so a queued war
        /// for another key may start from the next try on.</summary>
        internal static void AfterDefenseClock()
        {
            var ended = new List<MpRivalAttack>();
            lock (_lock)
                for (int i = _attacks.Count - 1; i >= 0; i--)
                {
                    if (StateAlive(_attacks[i])) continue;
                    ended.Insert(0, _attacks[i]);
                    _attacks.RemoveAt(i);
                }
            foreach (var a in ended)
            {
                try
                {
                    if (a.Mechanic == "LowDemand")
                    {
                        int added; lock (_lock) _copyAdded.TryGetValue(a.RivalId, out added);
                        Plugin.Logger.LogInfo($"[RivalWave] wave for {a.Key} on {a.RivalId} ended ({a.Opened?.Count ?? 0} shop(s) it opened stay; {added}/{LowDemandTotalCap} copycat shops added in {a.Nb}).");
                        continue;
                    }
                    bool gate = GateOn(a.RivalId, out int nk);
                    bool writeBack = gate || a.Key != HostKey;   // fold B3
                    int n = 0, wrote = 0, stillCut = 0;
                    float maxDev = 0f;
                    double sumPre = 0, sumNow = 0;
                    foreach (var it in a.Items)
                    {
                        var reg = GameStatePatcher.FindRegistration(it.Addr);
                        if (reg?.retailPrices == null || reg.businessOwnerRivalId != a.RivalId || it.Pre <= 0f) continue;
                        var rp = reg.retailPrices.FirstOrDefault(x => x != null && x.itemName == it.Item);
                        if (rp == null) continue;
                        n++;
                        float cur = rp.price;
                        if (cur <= it.Pre * ((1f - a.Cut) + 0.02f))
                        {
                            if (writeBack) { rp.price = it.Pre; cur = it.Pre; wrote++; }
                            else stillCut++;
                        }
                        float dev = System.Math.Abs(cur - it.Pre) / it.Pre * 100f;
                        if (dev > maxDev) maxDev = dev;
                        sumPre += it.Pre; sumNow += cur;
                    }
                    if (wrote > 0)
                    {
                        try { ItemHelper.ClearPriceCaches(); } catch { }
                        Plugin.Logger.LogWarning($"[RivalWar] war for {a.Key} on {a.RivalId}: {wrote} price(s) still at the cut after the game's re-price - written back to the pre-war value.");
                    }
                    int queued; string head;
                    lock (_lock) { queued = _warQ.TryGetValue(a.RivalId, out var q) ? q.Count : 0; head = queued > 0 ? _warQ[a.RivalId][0].Key : "-"; }
                    Plugin.Logger.LogInfo($"[RivalWar] restored {n}, max deviation {maxDev:F1}% (war for {a.Key} on {a.RivalId} ended {FmtMin(a.EndMin)}; avg {(n > 0 ? sumPre / n : 0):F2} before the war -> {(n > 0 ? sumNow / n : 0):F2} now; {wrote} written back{(stillCut > 0 ? $", {stillCut} still cut (gate off, the host's own war: native re-price only)" : "")}; gate {(gate ? "on" : "off")} ({nk} key(s)); queue {queued}, head {head}).");
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalWar] error in the restore check for {a.Key} on {a.RivalId}: {ex.GetType().Name}: {ex.Message}"); }
            }
            if (ended.Count > 0) MPServer.PublishRivalStateIfChanged("rivalwar-end");
        }

        // ── (e) copycat waves ──
        /// <summary>Prefix body on ActivateLowDemand (host), before the site guard's raise. With the manager's gate on: one
        /// wave per neighbourhood at a time (the previous wave's native state must have ended), a TOTAL of the game's own
        /// 10 added shops per neighbourhood, once per key per rival (the host key's due timeline entry is then completed
        /// silently). Then, always: refused when the aimed key has no shop there (native indexes [0] unchecked).</summary>
        internal static bool WaveGate(string nb, int aggr)
        {
            _wave = null;
            var rival = RivalsHelper.GetSpecialRivalByNeighborhood(nb);
            if (rival?.rivalData == null) return true;
            string rid = rival.rivalData.id ?? "";
            string me = AttackerKey;
            bool gate = GateOn(rid, out int nk);
            var nst = RivalsHelper.GetSpecialRivalState(rid);
            int added; lock (_lock) _copyAdded.TryGetValue(rid, out added);
            int capLeft = -1;
            if (gate)
            {
                if (nst?.defenseStates != null)
                    foreach (var ds in nst.defenseStates)
                        if (ds != null && ds.defensiveMechanic == DefensiveMechanic.LowDemand)
                        {
                            Plugin.Logger.LogInfo($"[RivalWave] wave for {me} on {rid} WAITS: the previous wave (for {OwnerOfState(rid, ds)}) runs until {FmtMin(EndMinOf(ds))}.");
                            return false;
                        }
                if (added >= LowDemandTotalCap)
                {
                    // Fold B5: at the cap no wave can ever open here - the due entry completes silently (as the once-per-key
                    // refusal below does) instead of re-planning daily. The spacing refusal above keeps re-planning.
                    string doneCap = me == HostKey && !_forcing ? CompleteHostWaveEntrySilently(rival, nst, aggr) : "";
                    Plugin.Logger.LogInfo($"[RivalWave] wave for {me} on {rid} refused: {added} of {LowDemandTotalCap} copycat shops already added in {nb} (total cap){(doneCap.Length > 0 ? $" - the due native entry {doneCap} completed silently" : "")}.");
                    return false;
                }
                if (CopycatDoneFor(me, rid))
                {
                    string done = me == HostKey && !_forcing ? CompleteHostWaveEntrySilently(rival, nst, aggr) : "";
                    Plugin.Logger.LogInfo($"[RivalWave] wave for {me} on {rid} refused: the key already had its copycat wave (once per key; {nk} keys counted){(done.Length > 0 ? $" - the due native entry {done} completed silently" : "")}.");
                    return false;
                }
                capLeft = LowDemandTotalCap - added;
            }
            if (KeyShopsIn(me, nb).Count == 0)
            {
                Plugin.Logger.LogInfo($"[RivalWave] wave for {me} on {rid} refused: the key has no shop in {nb}.");
                return false;
            }
            _wave = new WaveCtx { Key = me, Rid = rid, Nb = nb, Aggr = aggr, CapLeft = capLeft, Gate = gate, Before = nst?.defenseStates?.Count ?? 0 };
            return true;
        }

        /// <summary>The host key's due LowDemand entry (same aggression, not completed) is marked completed, so the
        /// game's own timeline stops re-planning it and speaks nothing (it never reaches CompleteEntry).</summary>
        private static string CompleteHostWaveEntrySilently(SpecialRival rival, SpecialRivalState nst, int aggr)
        {
            try
            {
                if (nst == null || rival.timeline?.allEntries == null) return "";
                if (nst.completedTimelineEntryIds == null) nst.completedTimelineEntryIds = new List<string>();
                foreach (var e in rival.timeline.allEntries)
                    if (e != null && e.defense == DefensiveMechanic.LowDemand && (int)e.aggression == aggr && !nst.completedTimelineEntryIds.Contains(e.id))
                    {
                        nst.completedTimelineEntryIds.Add(e.id);
                        return e.id;
                    }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalWave] error completing the host's wave entry: {ex.Message}"); }
            return "";
        }

        /// <summary>Postfix body on ActivateLowDemand: a wave that opened shops is recorded and counted.</summary>
        internal static void AfterWave(bool ok)
        {
            var w = _wave;
            if (w == null || !ok) return;
            int end = 0;
            var list = RivalsHelper.GetSpecialRivalState(w.Rid)?.defenseStates;
            if (list != null && list.Count > w.Before)
            {
                var ds = list[list.Count - 1];
                if (ds != null && ds.defensiveMechanic == DefensiveMechanic.LowDemand) end = EndMinOf(ds);
            }
            int total;
            lock (_lock)
            {
                _attacks.Add(new MpRivalAttack { Key = w.Key, RivalId = w.Rid, Nb = w.Nb, Mechanic = "LowDemand", Aggression = w.Aggr,
                                                 EntryId = _targetEntry ?? "", EndMin = end, Opened = new List<string>(w.Opened) });
                _copyAdded.TryGetValue(w.Rid, out total);
                total += w.Opened.Count;
                _copyAdded[w.Rid] = total;
            }
            SetCopycatDone(w.Key, w.Rid);
            Plugin.Logger.LogInfo($"[RivalWave] wave for {w.Key} on {w.Rid} OPENED {w.Opened.Count} shop(s) [{string.Join(" ; ", w.Opened)}] until {FmtMin(end)}; copycat shops added in {w.Nb}: {total}/{LowDemandTotalCap}; gate {(w.Gate ? "on" : "off")}{(w.CapLeft >= 0 ? $", wave cap {w.CapLeft}" : "")}.");
        }

        internal static void WaveDone() { _wave = null; }

        // ── (c) the mod path's firing ──
        /// <summary>One attack for a NON-host key through the game's own call, aimed at the key. Success: the entry is
        /// completed, the attack's own special message is taken off the shared SpecialMessagesTmpQueue (the game's
        /// CompleteEntry for the host key dequeues from the same queue) and routed as TEXT to the key's online members
        /// with the entry's own message (part C makes it spoken). Failure: nothing completed - the next hourly check
        /// plans it again, as native does.</summary>
        private static bool FireFor(string key, SpecialRival rival, DefensiveMechanic mech, int aggr, TimelineEntry entry, bool forced)
        {
            string rid = rival.rivalData.id ?? "", nb = rival.primaryNeighborhood ?? "";
            string eid = entry?.id ?? "force";
            ModPathFirings++;
            var st = Get(key, rid, true);
            if (mech == DefensiveMechanic.LowDemand && !forced && GateOn(rid, out int nk) && CopycatDoneFor(key, rid))
            {
                lock (_lock) if (!st.Completed.Contains(eid)) st.Completed.Add(eid);
                Plugin.Logger.LogInfo($"[RivalAttn] entry {eid} (LowDemand) for {key} on {rid} completed silently: the key already had its copycat wave (once per key, {nk} keys counted).");
                return true;
            }
            if (mech == DefensiveMechanic.LowDemand && !forced && GateOn(rid, out int nkc))
            {
                int addedC; lock (_lock) _copyAdded.TryGetValue(rid, out addedC);
                if (addedC >= LowDemandTotalCap)
                {
                    lock (_lock) if (!st.Completed.Contains(eid)) st.Completed.Add(eid);
                    Plugin.Logger.LogInfo($"[RivalAttn] entry {eid} (LowDemand) for {key} on {rid} completed silently: {addedC} of {LowDemandTotalCap} copycat shops already added in {nb} (total cap, fold B5; {nkc} keys counted).");
                    return true;
                }
            }
            var q = RivalDefenseHelper.SpecialMessagesTmpQueue;
            int qBefore = q.Count;
            bool ok = false;
            Target = key; _targetEntry = eid;
            try
            {
                if (mech == DefensiveMechanic.PriceReduction) ok = RivalDefenseHelper.ActivatePriceReduction(nb, (Enums.Priority)aggr);
                else if (mech == DefensiveMechanic.LowDemand) ok = RivalDefenseHelper.ActivateLowDemand(nb, (Enums.Priority)aggr);
                else Plugin.Logger.LogInfo($"[RivalAttn] entry {eid} ({mech}) for {key} on {rid}: employee poaching stays OFF in MP - not fired.");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] error firing {mech} for {key} on {rid}: {ex.GetType().Name}: {ex.Message}"); ok = false; }
            finally { Target = null; _targetEntry = null; }
            if (!ok)
            {
                Plugin.Logger.LogInfo($"[RivalAttn] fire {mech} for {key} on {rid} (entry {eid}): not done - nothing completed{(forced ? "" : "; the next hourly check plans it again")}.");
                return false;
            }
            Entities.TextMessage special = null;
            if (q.Count > qBefore)
            {
                var arr = q.ToArray();
                q.Clear();
                for (int i = 0; i < arr.Length - 1; i++) q.Enqueue(arr[i]);
                special = arr[arr.Length - 1];
            }
            if (entry != null) lock (_lock) if (!st.Completed.Contains(entry.id)) st.Completed.Add(entry.id);
            var pids = OnlinePidsOfKey(key);
            pids.Remove(MPConfig.PlayerId);
            // CompleteEntry (:241-250): the entry's message through SendMessageToPlayer and, in ITS callback, the attack's
            // special message with read = entry.messageClip != null. Part C: ONE rivalmono carrying both (the receiver's own
            // monologue first). The entry's message already sent: native runs the callback at once (:569-573) - the
            // special goes alone.
            bool entryClip = entry != null && entry.messageClip != null;
            List<RivalMonoTrailing> tr = null;
            if (special != null)
                tr = new List<RivalMonoTrailing> { new RivalMonoTrailing {
                    Key = special.messageKey,
                    Data = special.messageData == null ? new Dictionary<string, string>() : new Dictionary<string, string>(special.messageData),
                    Read = entryClip, Special = special.isSpecialMessage } };
            int t1 = 0, t2 = 0;
            string how = "none";
            bool mainSent = true;
            if (entry != null && !string.IsNullOrEmpty(entry.messageLocalizationKey))
                lock (_lock) mainSent = st.Sent.Contains(entry.messageLocalizationKey);
            if (!mainSent)
            {
                t1 = CompanyMessages.SendRivalMonoToPids(rival, entry.messageLocalizationKey, null, false, false, tr, pids);
                if (t1 > 0) { lock (_lock) st.Sent.Add(entry.messageLocalizationKey); t2 = tr != null ? t1 : 0; }
                how = entryClip ? "monologue" : "text";
            }
            else if (tr != null)
            {
                t2 = CompanyMessages.SendRivalMonoToPids(rival, tr[0].Key, tr[0].Data, tr[0].Read, tr[0].Special, null, pids);
                how = "special only";
            }
            Plugin.Logger.LogInfo($"[RivalAttn] FIRED {mech} ({(Enums.Priority)aggr}) for {key} on {rid} (entry {eid}{(entry != null ? ", completed" : ", forced - no entry")}): special message '{special?.messageKey ?? "-"}' taken off the shared queue; rivalmono ({how}) '{entry?.messageLocalizationKey ?? "-"}' to {t1} player(s), special to {t2}.");
            MPServer.PublishRivalStateIfChanged("rivalattn-fire");
            return true;
        }

        // ── part B patches ──
        [HarmonyPatch(typeof(RivalDefenseHelper), "ActivatePriceReduction")]
        public static class Patch_PriceWarQueue
        {
            static bool Prefix(string neighborhood, Enums.Priority aggression, ref bool __result, out WarCtx __state)
            {
                __state = null;
                try
                {
                    if (!MPServer.IsRunning) return true;
                    return WarGate(neighborhood, (int)aggression, ref __result, out __state);
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalWar] error in the war gate: {ex.GetType().Name}: {ex.Message}"); __state = null; return true; }
            }

            static void Postfix(bool __result, WarCtx __state)
            {
                try { if (__state != null) AfterWar(__state, __result); }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalWar] error recording the war: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(RivalDefenseHelper), "RunHourly")]
        public static class Patch_WarRestoreCheck
        {
            static void Postfix()
            {
                try { if (MPServer.IsRunning) AfterDefenseClock(); }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalWar] error in the hourly check: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(RivalDefenseHelper), "GetLowDemandValues")]
        public static class Patch_WaveCap
        {
            static void Postfix(ref int __result)
            {
                try
                {
                    var w = _wave;
                    if (w == null || w.CapLeft < 0 || __result <= w.CapLeft) return;
                    Plugin.Logger.LogInfo($"[RivalWave] wave for {w.Key} on {w.Rid} capped: {__result} -> {w.CapLeft} shop(s) (total cap {LowDemandTotalCap} per neighbourhood).");
                    __result = w.CapLeft;
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalWave] error in the wave cap: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Helpers.CompetitionHelper), "StartNewCompetitorBusiness")]
        public static class Patch_WaveOpened
        {
            static void Postfix(BuildingRegistration registration)
            {
                try
                {
                    var w = _wave;
                    if (w != null && registration != null) w.Opened.Add(GameStateReader.AddressKey(registration));
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalWave] error noting an opened shop: {ex.Message}"); }
            }
        }

        // ── rig levers ──────────────────────────────────────────────────────────────────────────
        private static SpecialRival FindRival(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return null;
            try
            {
                var r = RivalsHelper.GetSpecialRival(arg);
                if (r != null) return r;
                return RivalsHelper.GetSpecialRivalByNeighborhood(arg);
            }
            catch { return null; }
        }

        private static string Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }

        /// <summary>Store signature (planned entries and live counts excluded): the save/reload oracle.</summary>
        private static string StoreSig()
        {
            var parts = new List<string>();
            foreach (var e in Snapshot(true))
            {
                var c = new List<string>(e.Completed); c.Sort(System.StringComparer.Ordinal);
                var s = new List<string>(e.Sent); s.Sort(System.StringComparer.Ordinal);
                parts.Add($"{(e.IsHostKey ? "H" : e.Key)}|{e.RivalId}|{(e.IsActive ? 1 : 0)}|{string.Join(",", c)}|{string.Join(",", s)}");
            }
            parts.Sort(System.StringComparer.Ordinal);
            return Fnv(string.Join(";", parts)) + "/" + parts.Count;
        }

        /// <summary>`rivalattn [rival|nb]` (report), `rivalattn floor &lt;pid&gt; &lt;rival&gt; &lt;n&gt;` (DEV: the key's count reads at least n),
        /// `rivalattn report` (client: self-report now),
        /// `rivalattn sweep` (run the game's own hourly sweep now; the postfix follows it).</summary>
        internal static string Lever(string arg)
        {
            try
            {
                var tk = (arg ?? "").Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length >= 1 && tk[0] == "report")
                {
                    // CLIENT: send this machine's self-report now (the 120 s push, MPCanvasUI.TickRivalStatsPush).
                    if (MPServer.IsRunning || !MPClient.IsConnected) return "ERR client only";
                    MPClient.SendRivalsStatsRequest();
                    return "OK rivalattn report sent";
                }
                if (tk.Length >= 1 && tk[0] == "floor")
                {
                    if (!MPServer.IsRunning) return "ERR host only";
                    if (tk.Length != 4 || !int.TryParse(tk[3], out int nx)) return "ERR usage: rivalattn floor <pid> <rival> <n>";
                    var rx = FindRival(tk[2]);
                    if (rx == null) return $"ERR unknown rival '{tk[2]}'";
                    string kx = KeyOfPid(tk[1]);
                    if (kx.Length == 0) return $"ERR no stable id for '{tk[1]}'";
                    lock (_lock) { if (nx <= 0) _extra.Remove(kx + "|" + rx.rivalData.id); else _extra[kx + "|" + rx.rivalData.id] = nx; }
                    Plugin.Logger.LogInfo($"[RivalAttn] DEV: the count for {kx} on {rx.rivalData.id} reads at least {nx} (0 = the real count).");
                    return $"OK rivalattn floor key={kx} rival={rx.rivalData.id} n={nx}";
                }
                if (tk.Length >= 1 && tk[0] == "setactive")
                {
                    // DEV (part D): `rivalattn setactive <pid> <rival> 0|1` - the key of <pid> reads the rival active / not
                    // (the host key = the native isActive; any other key its stored state). No text, no timeline step.
                    if (!MPServer.IsRunning) return "ERR host only";
                    if (tk.Length != 4 || (tk[3] != "0" && tk[3] != "1")) return "ERR usage: rivalattn setactive <pid> <rival> 0|1";
                    var ra = FindRival(tk[2]);
                    if (ra == null) return $"ERR unknown rival '{tk[2]}'";
                    EnsureBound();
                    string ka = KeyOfPid(tk[1]);
                    if (ka.Length == 0) return $"ERR no stable id for '{tk[1]}'";
                    bool on = tk[3] == "1";
                    string rida = ra.rivalData.id;
                    if (ka == HostKey)
                    {
                        var nsa = RivalsHelper.GetSpecialRivalState(rida);
                        if (nsa == null) return $"ERR no native state for '{rida}'";
                        nsa.isActive = on;
                    }
                    else
                    {
                        var sa = Get(ka, rida, true);
                        lock (_lock) { sa.IsActive = on; sa.EverCounted = true; if (!on) _planned.RemoveAll(p => p.Key == ka && p.RivalId == rida); }
                    }
                    Plugin.Logger.LogInfo($"[RivalAttn] DEV: rival {rida} set {(on ? "ACTIVE" : "inactive")} for {ka}{(ka == HostKey ? " (host key: native state)" : "")}.");
                    MPServer.PublishRivalStateIfChanged("rivalattn-dev");
                    return $"OK rivalattn setactive key={ka} rival={rida} active={(on ? 1 : 0)} host={(ka == HostKey ? 1 : 0)}";
                }
                if (tk.Length >= 1 && tk[0] == "mono")
                {
                    // DEV (part C): monologues enqueued on THIS machine (any key), the last key, real defeats here.
                    return $"OK rivalattn mono enqueued={CompanyMessages.MonoEnqueued} last={(CompanyMessages.MonoLastKey.Length > 0 ? CompanyMessages.MonoLastKey : "-")} defeats={DefeatRuns}";
                }
                if (tk.Length >= 1 && tk[0] == "surrender")
                {
                    // DEV (part C): `rivalattn surrender <rival>` - the per-player surrender step now (its income gate
                    // skipped): after the native 10-30 s the host runs DefeatRival once; every active key hears it.
                    if (!MPServer.IsRunning) return "ERR host only";
                    if (tk.Length != 2) return "ERR usage: rivalattn surrender <rival>";
                    var rs = FindRival(tk[1]);
                    if (rs?.rivalData == null) return $"ERR unknown rival '{tk[1]}'";
                    EnsureBound();
                    var nss = RivalsHelper.GetSpecialRivalState(rs.rivalData.id);
                    if (nss == null) return $"ERR no native state for '{rs.rivalData.id}'";
                    if (nss.isDefeated) return $"ERR rival {rs.rivalData.id} already defeated";
                    ScheduleSurrender(rs.rivalData);
                    Plugin.Logger.LogInfo($"[RivalAttn] DEV: surrender of rival {rs.rivalData.id} scheduled on the per-player path (income gate skipped).");
                    return $"OK rivalattn surrender rival={rs.rivalData.id} scheduled";
                }
                if (tk.Length >= 1 && tk[0] == "hostchange")
                {
                    // DEV (fold B1 test): `rivalattn hostchange <p:key|g:key>` - SettleLoad's HOST-CHANGE branch now, as a hostload
                    // of this world saved while <key> hosted would run it: this host's native rows (as of now) become <key>'s
                    // stored rows, and this host's own stored rows are copied into the native state. Nothing is saved.
                    if (!MPServer.IsRunning) return "ERR host only";
                    if (tk.Length != 2 || tk[1].Length < 3 || !(tk[1].StartsWith("p:", System.StringComparison.Ordinal) || tk[1].StartsWith("g:", System.StringComparison.Ordinal)))
                        return "ERR usage: rivalattn hostchange <p:key|g:key>";
                    EnsureBound();
                    string hkNow = HostKey;
                    if (tk[1] == hkNow) return "ERR that is this host's own key";
                    RefreshHostRowsCache();
                    int nrows;
                    lock (_lock)
                    {
                        var rowsH = new List<MpRivalAttnEntry>();
                        foreach (var h in _hostRowsCache)
                            rowsH.Add(new MpRivalAttnEntry { Key = tk[1], RivalId = h.RivalId, IsActive = h.IsActive, IsHostKey = true, CopycatDone = _hostCopycat.Contains(h.RivalId),
                                                              Completed = new List<string>(h.Completed), Sent = new List<string>(h.Sent) });
                        _manifestHostRows = rowsH; _manifestHostKey = tk[1]; nrows = rowsH.Count;
                    }
                    SettleLoad();
                    RefreshHostRowsCache();
                    Plugin.Logger.LogInfo($"[RivalAttn] DEV: host change replayed: {nrows} native row(s) now belong to {tk[1]} (a previous host), this host {hkNow} took its stored rows.");
                    MPServer.PublishRivalStateIfChanged("rivalattn-dev");
                    return $"OK rivalattn hostchange from={tk[1]} to={hkNow} rows={nrows}";
                }
                if (tk.Length >= 1 && tk[0] == "sweep")
                {
                    if (!MPServer.IsRunning) return "ERR host only";
                    RivalsHelper.CheckRivalTimelines();
                    return "OK rivalattn sweep ran";
                }
                SpecialRival only = tk.Length >= 1 ? FindRival(tk[0]) : null;
                if (tk.Length >= 1 && only == null) return $"ERR unknown rival '{tk[0]}'";
                var rows = new List<string>();
                if (!MPServer.IsRunning)
                {
                    // CLIENT: the game's own GetPlayerValues on THIS machine, beside what this machine reports.
                    RivalSelfStats.Build(out int _, out float _, out string _, out var myRows);
                    foreach (var rival in RivalsHelper.GetSpecialRivals())
                    {
                        if (rival?.rivalData == null) continue;
                        if (only != null && rival != only) continue;
                        string nb = rival.primaryNeighborhood ?? "";
                        int nc = -1; float ni = 0f;
                        try { var pv = rival.timeline.GetPlayerValues(rival); nc = pv.Item1?.Count ?? 0; ni = pv.Item2; } catch { }
                        int rq = myRows.Count(r => r != null && r.RivalQualifying && r.Neighborhood == nb);
                        var st = RivalsHelper.GetSpecialRivalState(rival.rivalData.id);
                        bool rentSent = st?.sentMessageKeys != null && !string.IsNullOrEmpty(rival.rentBuildingMessageKey) && st.sentMessageKeys.Contains(rival.rentBuildingMessageKey);
                        rows.Add($"{rival.rivalData.id}{{{nb}}}: native count={nc} income={ni:F0} reported={rq} active={(st?.isActive == true ? 1 : 0)} sent={st?.sentMessageKeys?.Count ?? 0} completed={st?.completedTimelineEntryIds?.Count ?? 0} rentsent={(rentSent ? 1 : 0)}");
                    }
                    return $"OK rivalattn local pid={MPConfig.PlayerId} rivals=[{string.Join(" ; ", rows)}]";
                }
                EnsureBound();
                string hk = HostKey;
                var online = NonHostKeysOnline();
                foreach (var rival in RivalsHelper.GetSpecialRivals())
                {
                    if (rival?.rivalData == null) continue;
                    if (only != null && rival != only) continue;
                    string rid = rival.rivalData.id ?? "";
                    float wk = 0f; try { wk = rival.rivalData.WeeklyIncome; } catch { }
                    var parts = new List<string>();
                    int hc = -1; float hi = 0f;
                    try { var pv = rival.timeline.GetPlayerValues(rival); hc = pv.Item1?.Count ?? 0; hi = pv.Item2; } catch { }
                    var nst = RivalsHelper.GetSpecialRivalState(rid);
                    parts.Add($"{hk}{{host}}|count={hc}|income={hi:F0}|pct={(wk > 0 ? hi / wk * 100f : 0f):F1}|active={(nst?.isActive == true ? 1 : 0)}|completed={nst?.completedTimelineEntryIds?.Count ?? 0}|sent={nst?.sentMessageKeys?.Count ?? 0}|native");
                    var keys = new List<string>(online);
                    lock (_lock)
                        foreach (var kv in _store)
                            if (kv.Key.EndsWith("|" + rid, System.StringComparison.Ordinal))
                            {
                                string k = kv.Key.Substring(0, kv.Key.Length - rid.Length - 1);
                                if (!keys.Contains(k)) keys.Add(k);
                            }
                    keys.Sort(System.StringComparer.Ordinal);
                    foreach (var k in keys)
                    {
                        if (k == hk) continue;
                        CountFor(k, rival, out int c, out float inc, out int ex);
                        var st = Get(k, rid, false);
                        int planned;
                        string act, comp, sent, ever;
                        lock (_lock)
                        {
                            planned = _planned.Count(p => p.Key == k && p.RivalId == rid);
                            act = st != null && st.IsActive ? "1" : "0";
                            comp = (st?.Completed.Count ?? 0).ToString();
                            sent = (st?.Sent.Count ?? 0).ToString();
                            ever = st != null && st.EverCounted ? "1" : "0";
                        }
                        var pids = new List<string>();
                        foreach (var s in StablesOfKey(k)) pids.AddRange(PidsOfStable(s));
                        parts.Add($"{k}{{{string.Join(",", pids)}}}|count={c}|floor={ex}|income={inc:F0}|pct={(wk > 0 ? inc / wk * 100f : 0f):F1}|active={act}|completed={comp}|sent={sent}|planned={planned}|ever={ever}|online={(online.Contains(k) ? 1 : 0)}");
                    }
                    rows.Add($"{rid}{{{rival.primaryNeighborhood}}} wk={wk:F0}: {string.Join(" ; ", parts)}");
                }
                return $"OK rivalattn host hostKey={hk} keys={online.Count} firings={ModPathFirings} sig={StoreSig()} rivals=[{string.Join(" || ", rows)}]";
            }
            catch (System.Exception ex) { return $"ERR rivalattn: {ex.GetType().Name}: {ex.Message}"; }
        }

        /// <summary>DEV (part D) `rivalrent`:
        ///   find &lt;rival&gt;        - for-rent buildings the rival OWNS on this machine (the field the game's gate reads);
        ///   gate &lt;addr&gt;         - the game's own rent gate on THIS machine, no side effect;
        ///   try &lt;addr&gt;          - the game's own gate WITH its side effect: blocked = RivalsHelper.SendRentBuildingMessage
        ///                         (the rival's own monologue, recorded in this machine's sent keys), nothing rented;
        ///   force &lt;addr&gt;        - CLIENT: rent past the local gate (BuildingHelper.RentBuilding, the `rent` lever's path)
        ///                         so the host's authority gate answers (a refusal rolls the local rent back);
        ///   host &lt;addr&gt; &lt;pid&gt;   - HOST: the authority gate for that player's key, no side effect beyond its log line;
        ///   buy &lt;addr&gt;          - CLIENT (fold R3): the game's own buy core (BizManPresentation.SendBuyBuildingOffer :937-954:
        ///                         realEstate entry, off the for-sale list, the rival owner field cleared) with NO money and
        ///                         no UI, then the mod's BuyRequest to the host.</summary>
        internal static string RentLever(string arg)
        {
            try
            {
                var tk = (arg ?? "").Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length < 2) return "ERR usage: rivalrent find <rival> | gate <addr> | try <addr> | force <addr> | host <addr> <pid> | buy <addr>";
                string verb = tk[0];
                if (verb == "find")
                {
                    var rv = FindRival(tk[1]);
                    if (rv == null) return $"ERR unknown rival '{tk[1]}'";
                    string rid = rv.rivalData.id;
                    var gi = SaveGameManager.Current;
                    var hits = new List<string>();
                    int owned = 0;
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        SpecialRival br = null;
                        try { br = RivalsHelper.GetSpecialRival(reg.buildingOwnerRivalId); } catch { }
                        if (br?.rivalData?.id != rid) continue;
                        owned++;
                        bool free = false; try { free = reg.AvailableForRent && !reg.RentedByPlayer && !reg.BuildingOwnedByPlayer; } catch { }
                        if (free) hits.Add(GameStateReader.AddressKey(reg));
                    }
                    hits.Sort(System.StringComparer.Ordinal);
                    return $"OK rivalrent find rival={rid} owned={owned} forrent={hits.Count} first={(hits.Count > 0 ? hits[0] : "-")} [{string.Join(" ; ", hits)}]";
                }
                // every other verb: <addr> = the rest of the tokens (host: the last token is the pid)
                string addr = verb == "host" && tk.Length >= 3 ? string.Join(" ", tk, 1, tk.Length - 2) : string.Join(" ", tk, 1, tk.Length - 1);
                var rg = GameStatePatcher.FindRegistration(addr);
                if (rg == null) return $"ERR no registration for '{addr}'";
                bool isFree = false; try { isFree = rg.AvailableForRent && !rg.RentedByPlayer; } catch { }
                if (verb == "host")
                {
                    if (!MPServer.IsRunning) return "ERR host only";
                    string pid = tk[tk.Length - 1];
                    bool refuse = HostRefusesFor(rg, addr, pid, "rent", true, out string hr);
                    return $"OK rivalrent host={(refuse ? "refused" : "allowed")} addr='{addr}' pid={pid} key={KeyOfPid(pid)} rival={(hr.Length > 0 ? hr : "-")}";
                }
                if (verb == "buy")
                {
                    if (MPServer.IsRunning || !MPClient.IsConnected) return "ERR client only";
                    if (rg.BuildingOwnedByPlayer) return $"ERR '{addr}' is already bought on this machine";
                    var bb = Helpers.BuildingHelper.GetBuilding(rg.Address);
                    if (bb == null) return $"ERR no Building for '{addr}'";
                    var gib = SaveGameManager.Current;
                    string was = rg.buildingOwnerRivalId ?? "";
                    if (gib.realEstate == null) gib.realEstate = new List<Entities.RealEstate>();
                    gib.realEstate.Add(new Entities.RealEstate { address = rg.Address, purchasePrice = 0.0, purchaseDay = gib.Day, totalSqm = bb.totalSqm,
                                                                 occupancy = 50f, pricePerSqm = bb.GetBuildingDailyMarketRentPerSqm() });
                    var bfs = gib.buildingsForSale?.FirstOrDefault(x => x != null && x.address == rg.Address);
                    if (bfs != null) gib.buildingsForSale.Remove(bfs);
                    if (rg.RentedByPlayer) rg.RentPerDay = 0f;
                    rg.buildingOwnerRivalId = string.Empty;
                    MPClient.RequestBuyBuilding(addr);
                    Plugin.Logger.LogInfo($"[RivalSync] DEV: '{addr}' bought on this machine (no money) - rival owner '{(was.Length > 0 ? was : "-")}' cleared, BuyRequest sent.");
                    return $"OK rivalrent buy sent addr='{addr}' wasrival={(was.Length > 0 ? was : "-")} owned={rg.BuildingOwnedByPlayer}";
                }
                // The game's own gate (BizManPresentation.cs:538-551), on THIS machine's state.
                SpecialRival gr = null;
                bool blocked = false;
                if (RivalsHelper.IsFeatureEnabled && !rg.BuildingOwnedByPlayer)
                {
                    gr = RivalsHelper.GetSpecialRival(rg.buildingOwnerRivalId);
                    blocked = gr != null && RivalsHelper.GetSpecialRivalState(gr.rivalData.id).isActive;
                }
                string gid = gr?.rivalData?.id ?? "-";
                if (verb == "gate")
                    return $"OK rivalrent gate={(blocked ? "blocked" : "allowed")} addr='{addr}' rival={gid} free={isFree} pid={MPConfig.PlayerId}";
                if (verb == "try")
                {
                    if (!blocked) return $"OK rivalrent try=allowed addr='{addr}' rival={gid} (nothing rented)";
                    RivalsHelper.SendRentBuildingMessage(gr);
                    Plugin.Logger.LogInfo($"[RivalSync] rent of '{addr}' refused by this machine's own gate: rival '{gid}' is active here (the game's own rent-building message sent, key '{gr.rentBuildingMessageKey}').");
                    return $"OK rivalrent try=blocked addr='{addr}' rival={gid} msg={gr.rentBuildingMessageKey}";
                }
                if (verb == "force")
                {
                    if (MPServer.IsRunning || !MPClient.IsConnected) return "ERR client only";
                    bool ownBought = false; try { ownBought = rg.BuildingOwnedByPlayer && !rg.RentedByPlayer; } catch { }
                    if (!isFree && !ownBought) return $"ERR '{addr}' is not on the for-rent market on this machine";
                    var bld = Helpers.BuildingHelper.GetBuilding(rg.Address);
                    if (bld == null) return $"ERR no Building for '{addr}'";
                    float rent = 0f;
                    if (ownBought) Helpers.BuildingHelper.RentBuilding(bld, 0f, 0f);   // a bought building: no rent (the game's own Hamptons path, :962)
                    else
                    {
                        try { rent = bld.GetBuildingDailyMarketRent(); } catch { }
                        if (rent <= 0f) rent = 100f;
                        Helpers.BuildingHelper.RentBuilding(bld, rent, rent * 90f);
                    }
                    return $"OK rivalrent force sent addr='{addr}' localgate={(blocked ? "blocked" : "allowed")} rival={gid} bought={(ownBought ? 1 : 0)}";
                }
                return $"ERR unknown verb '{verb}'";
            }
            catch (System.Exception ex) { return $"ERR rivalrent: {ex.GetType().Name}: {ex.Message}"; }
        }

        /// <summary>Part B DEV `rivalforce &lt;rival|nb&gt; &lt;pid|key|host&gt; price|lowdemand [low|medium|high]` (HOST): one attack
        /// aimed at that key through the game's own call - the host key directly (the native path's gates apply), any
        /// other key through the mod path (FireFor, no timeline entry). Default aggression High.</summary>
        internal static string ForceLever(string arg)
        {
            try
            {
                if (!MPServer.IsRunning) return "ERR host only";
                var tk = (arg ?? "").Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length < 3) return "ERR usage: rivalforce <rival|nb> <pid|key|host> price|lowdemand [low|medium|high] [entry]";
                var rival = FindRival(tk[0]);
                if (rival?.rivalData == null) return $"ERR unknown rival '{tk[0]}'";
                EnsureBound();
                string who = tk[1];
                string key = who == "host" ? HostKey : (who.StartsWith("p:", System.StringComparison.Ordinal) || who.StartsWith("g:", System.StringComparison.Ordinal)) ? who : KeyOfPid(who);
                if (key.Length == 0) return $"ERR no key for '{who}'";
                DefensiveMechanic mech;
                if (tk[2] == "price") mech = DefensiveMechanic.PriceReduction;
                else if (tk[2] == "lowdemand") mech = DefensiveMechanic.LowDemand;
                else return "ERR mechanic must be price|lowdemand";
                Enums.Priority pr = Enums.Priority.High;
                bool withEntry = false;
                for (int ti = 3; ti < tk.Length; ti++)
                {
                    if (tk[ti] == "entry") { withEntry = true; continue; }
                    if (!System.Enum.TryParse(tk[ti], true, out pr)) return $"ERR unknown aggression '{tk[ti]}'";
                }
                string rid = rival.rivalData.id, nb = rival.primaryNeighborhood ?? "";
                bool ok;
                if (key == HostKey)
                {
                    var fq = RivalDefenseHelper.SpecialMessagesTmpQueue;
                    int fqBefore = fq.Count;
                    _forcing = true;
                    try
                    {
                        ok = mech == DefensiveMechanic.PriceReduction ? RivalDefenseHelper.ActivatePriceReduction(nb, pr) : RivalDefenseHelper.ActivateLowDemand(nb, pr);
                    }
                    finally { _forcing = false; }
                    if (ok && fq.Count > fqBefore)
                    {
                        // Fold B7: no timeline entry completes a forced attack, so nothing would dequeue its special message
                        // (native CompleteEntry does) - it is taken off the shared queue as the mod path (FireFor) does.
                        // Fold E8: EVERY special message the call added (all past the count taken before it) goes.
                        var arr = fq.ToArray();
                        fq.Clear();
                        var taken = new List<string>();
                        for (int i = 0; i < arr.Length; i++)
                        {
                            if (i < fqBefore) fq.Enqueue(arr[i]);
                            else taken.Add(arr[i]?.messageKey ?? "-");
                        }
                        Plugin.Logger.LogInfo($"[RivalAttn] DEV: rivalforce for the host key: {taken.Count} special message(s) [{string.Join(", ", taken)}] taken off the shared queue (no entry completes a forced attack).");
                    }
                }
                else
                {
                    // part C (DEV): `entry` = the key's first uncompleted timeline entry of that mechanic is fired and
                    // completed, so its own message (and clip) goes with the attack, exactly as a planned entry's does.
                    TimelineEntry fe = null;
                    if (withEntry)
                    {
                        var stf = Get(key, rid, true);
                        var all = rival.timeline?.allEntries;
                        if (all != null)
                            foreach (var e in all)
                            {
                                if (e == null || e.defense != mech) continue;
                                bool done;
                                lock (_lock) done = stf.Completed.Contains(e.id);
                                if (!done) { fe = e; break; }
                            }
                        if (fe == null) return $"ERR no uncompleted {tk[2]} entry for {key} on {rid}";
                    }
                    ok = FireFor(key, rival, mech, fe != null ? (int)fe.aggression : (int)pr, fe, true);
                }
                int pos = 0;
                lock (_lock) if (_warQ.TryGetValue(rid, out var q)) pos = q.FindIndex(x => x.Key == key) + 1;
                Plugin.Logger.LogInfo($"[RivalAttn] DEV: rivalforce {tk[2]} ({pr}) for {key} on {rid}: result={ok}, queue position {pos}.");
                return $"OK rivalforce key={key} rival={rid} mech={tk[2]} aggr={pr} result={ok} queued={pos} host={(key == HostKey ? 1 : 0)}";
            }
            catch (System.Exception ex) { return $"ERR rivalforce: {ex.GetType().Name}: {ex.Message}"; }
        }

        /// <summary>Part B `rivalq [rival|nb]` (HOST): running attacks with their keys, the war queue, the copycat count
        /// and flags, the gate; war=&lt;key&gt;:&lt;items&gt; = the first running price war (for a capture); wsig = the
        /// attribution signature (the save/reload oracle). `rivalq end &lt;rival&gt; price|lowdemand` (DEV): the rival's
        /// running native states of that mechanic end NOW (their records follow), so the next game hour ends them.</summary>
        internal static string QueueLever(string arg)
        {
            try
            {
                if (!MPServer.IsRunning) return "ERR host only";
                EnsureBound();
                var tk = (arg ?? "").Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length >= 1 && tk[0] == "end")
                {
                    if (tk.Length != 3) return "ERR usage: rivalq end <rival> price|lowdemand";
                    var re = FindRival(tk[1]);
                    if (re?.rivalData == null) return $"ERR unknown rival '{tk[1]}'";
                    var mechE = tk[2] == "price" ? DefensiveMechanic.PriceReduction : tk[2] == "lowdemand" ? DefensiveMechanic.LowDemand : DefensiveMechanic.None;
                    if (mechE == DefensiveMechanic.None) return "ERR mechanic must be price|lowdemand";
                    var nsE = RivalsHelper.GetSpecialRivalState(re.rivalData.id);
                    int moved = 0;
                    if (nsE?.defenseStates != null)
                        foreach (var ds in nsE.defenseStates)
                        {
                            if (ds == null || ds.defensiveMechanic != mechE) continue;
                            int old = EndMinOf(ds);
                            ds.timestamp = TimeHelper.Now();
                            int now = EndMinOf(ds);
                            lock (_lock) foreach (var a in _attacks) if (a.RivalId == re.rivalData.id && a.Mechanic == mechE.ToString() && a.EndMin == old) a.EndMin = now;
                            moved++;
                        }
                    Plugin.Logger.LogInfo($"[RivalAttn] DEV: {moved} running {mechE} state(s) of {re.rivalData.id} end now ({FmtMin(NowTotalMin())}) - the next game hour ends them.");
                    MPServer.PublishRivalStateIfChanged("rivalq-end");
                    return $"OK rivalq end rival={re.rivalData.id} mech={mechE} moved={moved}";
                }
                if (tk.Length >= 1 && tk[0] == "added")
                {
                    // DEV (part B): `rivalq added <rival> <n>` - the copycat shops counted as already added for the rival.
                    if (tk.Length != 3 || !int.TryParse(tk[2], out int na) || na < 0) return "ERR usage: rivalq added <rival> <n>";
                    var ra = FindRival(tk[1]);
                    if (ra?.rivalData == null) return $"ERR unknown rival '{tk[1]}'";
                    lock (_lock) { if (na == 0) _copyAdded.Remove(ra.rivalData.id); else _copyAdded[ra.rivalData.id] = na; }
                    Plugin.Logger.LogInfo($"[RivalAttn] DEV: copycat shops added for {ra.rivalData.id} set to {na}/{LowDemandTotalCap}.");
                    return $"OK rivalq added rival={ra.rivalData.id} n={na}";
                }
                SpecialRival only = tk.Length >= 1 ? FindRival(tk[0]) : null;
                if (tk.Length >= 1 && only == null) return $"ERR unknown rival '{tk[0]}'";
                var rows = new List<string>();
                var sigParts = new List<string>();
                string war = "-";
                foreach (var rival in RivalsHelper.GetSpecialRivals())
                {
                    if (rival?.rivalData == null) continue;
                    if (only != null && rival != only) continue;
                    string rid = rival.rivalData.id;
                    bool gate = GateOn(rid, out int nk);
                    var atk = new List<string>();
                    var qs = new List<string>();
                    var cc = new List<string>();
                    int added, native = 0;
                    var nst = RivalsHelper.GetSpecialRivalState(rid);
                    if (nst?.defenseStates != null)
                        foreach (var ds in nst.defenseStates) if (ds != null && ds.defensiveMechanic != DefensiveMechanic.None) native++;
                    lock (_lock)
                    {
                        foreach (var a in _attacks)
                        {
                            if (a.RivalId != rid) continue;
                            var items = new List<string>();
                            foreach (var it in a.Items) if (!items.Contains(it.Item)) items.Add(it.Item);
                            items.Sort(System.StringComparer.Ordinal);
                            atk.Add($"{a.Key}/{a.Mechanic}/{(Enums.Priority)a.Aggression}/end={FmtMin(a.EndMin)}/entry={(a.EntryId.Length > 0 ? a.EntryId : "native")}/items={string.Join(",", items)}/rows={a.Items.Count}/opened={a.Opened?.Count ?? 0}");
                            sigParts.Add($"a|{rid}|{a.Key}|{a.Mechanic}|{a.EndMin}|{string.Join(",", items)}|{a.Items.Count}|{a.Opened?.Count ?? 0}");
                            if (war == "-" && a.Mechanic == "PriceReduction" && StateAlive(a)) war = $"{a.Key}:{string.Join(",", items)}";
                        }
                        if (_warQ.TryGetValue(rid, out var q))
                            for (int i = 0; i < q.Count; i++) { qs.Add($"{i + 1}:{q[i].Key}"); sigParts.Add($"q|{rid}|{i}|{q[i].Key}"); }
                        _copyAdded.TryGetValue(rid, out added);
                        sigParts.Add($"c|{rid}|{added}");
                        if (_hostCopycat.Contains(rid)) cc.Add(HostKey);
                        foreach (var kv in _store) if (kv.Value.CopycatDone && kv.Key.EndsWith("|" + rid, System.StringComparison.Ordinal)) cc.Add(kv.Key.Substring(0, kv.Key.Length - rid.Length - 1));
                    }
                    rows.Add($"{rid}{{{rival.primaryNeighborhood}}}: gate={(gate ? 1 : 0)}({nk}) native={native} added={added}/{LowDemandTotalCap} copycat=[{string.Join(",", cc)}] attacks=[{string.Join(" ; ", atk)}] queue=[{string.Join(",", qs)}]");
                }
                sigParts.Sort(System.StringComparer.Ordinal);
                return $"OK rivalq hostKey={HostKey} firings={ModPathFirings} war={war} wsig={Fnv(string.Join(";", sigParts))}/{sigParts.Count} rivals=[{string.Join(" || ", rows)}]";
            }
            catch (System.Exception ex) { return $"ERR rivalq: {ex.GetType().Name}: {ex.Message}"; }
        }

        /// <summary>Part B `rivalprices &lt;rival|nb&gt; [item,item,...]` (either machine): the rival's retail prices on THIS
        /// machine (every shop it owns, city-wide - the rows a war cuts), optionally only the named items: rows, a
        /// signature over address|item|price (2 decimals) and per item avg/min/max. Equal sig on host and client =
        /// equal prices.</summary>
        internal static string PricesLever(string arg)
        {
            try
            {
                var tk = (arg ?? "").Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length < 1) return "ERR usage: rivalprices <rival|nb> [item,item,...]";
                var rival = FindRival(tk[0]);
                if (rival?.rivalData == null) return $"ERR unknown rival '{tk[0]}'";
                string rid = rival.rivalData.id;
                HashSet<string> filter = null;
                if (tk.Length >= 2 && tk[1] != "-") filter = new HashSet<string>(tk[1].Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries), System.StringComparer.Ordinal);
                var lines = new List<string>();
                var stat = new Dictionary<string, List<float>>(System.StringComparer.Ordinal);
                foreach (var reg in SaveGameManager.Current.BuildingRegistrations)
                {
                    if (reg == null || reg.businessOwnerRivalId != rid || reg.retailPrices == null) continue;
                    string addr = GameStateReader.AddressKey(reg);
                    foreach (var rp in reg.retailPrices)
                    {
                        if (rp == null || string.IsNullOrEmpty(rp.itemName)) continue;
                        if (filter != null && !filter.Contains(rp.itemName)) continue;
                        lines.Add($"{addr}|{rp.itemName}|{F2(rp.price)}");
                        if (!stat.TryGetValue(rp.itemName, out var l)) { l = new List<float>(); stat[rp.itemName] = l; }
                        l.Add(rp.price);
                    }
                }
                lines.Sort(System.StringComparer.Ordinal);
                var names = new List<string>(stat.Keys);
                names.Sort(System.StringComparer.Ordinal);
                var parts = new List<string>();
                foreach (var nm in names)
                {
                    if (filter == null && parts.Count >= 6) break;
                    var l = stat[nm];
                    parts.Add($"{nm}:n={l.Count},avg={F2(l.Average())},min={F2(l.Min())},max={F2(l.Max())}");
                }
                return $"OK rivalprices rival={rid} rows={lines.Count} sig={Fnv(string.Join(";", lines))} pid={MPConfig.PlayerId} items=[{string.Join(" ; ", parts)}]";
            }
            catch (System.Exception ex) { return $"ERR rivalprices: {ex.GetType().Name}: {ex.Message}"; }
        }

        /// <summary>`rivaltimeline &lt;rival|nb&gt;`: the rival's allEntries (asset data, not in the decompile).</summary>
        internal static string TimelineLever(string arg)
        {
            try
            {
                var rival = FindRival((arg ?? "").Trim());
                if (rival == null) return $"ERR usage: rivaltimeline <rivalId|neighbourhood> (unknown '{arg}')";
                var tl = rival.timeline;
                var rows = new List<string>();
                int i = 0;
                if (tl?.allEntries != null)
                    foreach (var e in tl.allEntries)
                    {
                        if (e == null) { rows.Add($"#{i++}:null"); continue; }
                        bool done = false;
                        try { done = e.IsCompleted; } catch { }
                        rows.Add($"#{i++}:{e.defense}/{e.aggression}|biz>={e.businesses}|pct>={e.weeklyIncomePercentage}|id={e.id}|msg={e.messageLocalizationKey}|clip={(e.messageClip != null ? 1 : 0)}|nativeDone={(done ? 1 : 0)}");
                    }
                return $"OK rivaltimeline rival={rival.rivalData?.id} nb={rival.primaryNeighborhood} entries={tl?.allEntries?.Count ?? 0} entrance={rival.entranceMessageKey} act={tl?.activationMessageKey} deact={tl?.deactivationMessageKey} surrender={tl?.surrenderMessageKey} [{string.Join(" ; ", rows)}]";
            }
            catch (System.Exception ex) { return $"ERR rivaltimeline: {ex.GetType().Name}: {ex.Message}"; }
        }
    }
}
