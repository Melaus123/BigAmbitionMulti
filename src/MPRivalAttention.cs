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
    /// PART A IS A DRY RUN FOR ATTACKS: a due entry only logs "[RivalAttn] would fire ..." and nothing is marked
    /// completed (part B makes them real). Poaching stays OFF.
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
                    foreach (var pid in PidsOfStable(stable))
                        foreach (var row in MPServer.SelfReportRows(pid))
                        {
                            if (row == null || !row.RivalQualifying || row.Neighborhood != nb) continue;
                            if (!seen.Add(row.AddressKey ?? "")) continue;
                            count++;
                            income += row.WeeklyIncome;
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
            if (ReferenceEquals(gi, _boundGi)) return true;
            lock (_lock)
            {
                if (_armed)
                {
                    if (ReferenceEquals(gi, _giAtRestore)) return false;   // the pre-load world is still current
                    _armed = false;
                }
                else
                {
                    _store.Clear(); _planned.Clear(); _pending.Clear(); _extra.Clear();
                    _manifestHostRows = null; _manifestHostKey = "";
                    _migrationPending = true;
                }
                _boundGi = gi;
            }
            return true;
        }

        /// <summary>MPSaveCoordinator load path, beside RestoreOwnershipFromManifest: clear-then-apply from THIS
        /// manifest (same timeline rule as the absence marks). NULL = a manifest older than this feature =
        /// migration: the host key IS the native state already; every other player starts fresh.</summary>
        public static void RestoreFromManifest(MpManifest m)
        {
            try
            {
                int n = 0, keys = 0, host = 0;
                lock (_lock)
                {
                    _store.Clear(); _planned.Clear(); _pending.Clear(); _extra.Clear();
                    _manifestHostRows = null; _manifestHostKey = ""; _migrationPending = false;
                    var seenKeys = new HashSet<string>(System.StringComparer.Ordinal);
                    if (m?.RivalAttention == null) _migrationPending = true;
                    else
                        foreach (var e in m.RivalAttention)
                        {
                            if (e == null || string.IsNullOrEmpty(e.Key) || string.IsNullOrEmpty(e.RivalId)) continue;
                            if (e.IsHostKey)
                            {
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
                    Plugin.Logger.LogInfo($"[RivalAttn] restored {n} key-rival row(s) for {keys} key(s) from the manifest (+{host} host row(s), host key '{_manifestHostKey}').");
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
                if (refreshHost) RefreshHostRowsCache();
                lock (_lock)
                {
                    foreach (var h in _hostRowsCache)
                        list.Add(new MpRivalAttnEntry { Key = h.Key, RivalId = h.RivalId, IsActive = h.IsActive, IsHostKey = true,
                                                        Completed = new List<string>(h.Completed), Sent = new List<string>(h.Sent) });
                    foreach (var kv in _store)
                    {
                        int bar = kv.Key.LastIndexOf('|');
                        if (bar <= 0) continue;
                        var st = kv.Value;
                        list.Add(new MpRivalAttnEntry { Key = kv.Key.Substring(0, bar), RivalId = kv.Key.Substring(bar + 1), IsActive = st.IsActive,
                                                        CopycatDone = st.CopycatDone, EverCounted = st.EverCounted,
                                                        Completed = new List<string>(st.Completed), Sent = new List<string>(st.Sent) });
                    }
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
                lock (_lock) { hostRows = _manifestHostRows; mk = _manifestHostKey; _manifestHostRows = null; }
                if (hostRows == null || mk == hk) return;
                // A different person hosted when this manifest was written.
                lock (_lock)
                    foreach (var e in hostRows)
                        _store[e.Key + "|" + e.RivalId] = new State { IsActive = e.IsActive, EverCounted = true,
                                                                      Completed = new List<string>(e.Completed ?? new List<string>()),
                                                                      Sent = new List<string>(e.Sent ?? new List<string>()) };
                int moved = 0;
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
                    lock (_lock) _store.Remove(hk + "|" + rid);
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
                    Schedule($"{key}|{rid}|deact", () => { if (DeliverOnce(key, rival, tl.deactivationMessageKey, "deactivation")) SetActive(key, rival, false); });
                else if (!active && n >= 3)
                    Schedule($"{key}|{rid}|act", () => { if (DeliverOnce(key, rival, tl.activationMessageKey, "activation")) SetActive(key, rival, true); });
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

        /// <summary>SendMessageToPlayer (:164-211) for a key: a key already in Sent only runs the follow-up (true);
        /// otherwise the TEXT goes to the key's online members (part A: text only) and the key is marked sent.
        /// Nobody online = nothing sent, nothing marked, no follow-up - the next check tries again.</summary>
        private static bool DeliverOnce(string key, SpecialRival rival, string msgKey, string what)
        {
            try
            {
                if (string.IsNullOrEmpty(msgKey)) return true;
                string rid = rival.rivalData?.id ?? "";
                var st = Get(key, rid, true);
                lock (_lock) { if (st.Sent.Contains(msgKey)) return true; }
                var pids = OnlinePidsOfKey(key);
                pids.Remove(MPConfig.PlayerId);
                int n = CompanyMessages.SendRivalTextToPids(rival, msgKey, false, pids);
                if (n == 0)
                {
                    Plugin.Logger.LogInfo($"[RivalAttn] {what} '{msgKey}' for {key} on {rid}: no member online - not sent, retried on the next check.");
                    return false;
                }
                lock (_lock) st.Sent.Add(msgKey);
                Plugin.Logger.LogInfo($"[RivalAttn] {what} '{msgKey}' for {key} on {rid}: text to {n} player(s) [{string.Join(",", pids)}].");
                return true;
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] {what} for {key}: {ex.Message}"); return false; }
        }

        /// <summary>ActivateRival / DeactivateRival (:187-203) for a key: flip, then the game's own silent special text.</summary>
        private static void SetActive(string key, SpecialRival rival, bool on)
        {
            try
            {
                string rid = rival.rivalData?.id ?? "";
                var st = Get(key, rid, true);
                lock (_lock)
                {
                    if (st.IsActive == on) return;
                    st.IsActive = on;
                    if (!on) _planned.RemoveAll(p => p.Key == key && p.RivalId == rid);
                }
                var pids = OnlinePidsOfKey(key);
                pids.Remove(MPConfig.PlayerId);
                int n = CompanyMessages.SendRivalTextToPids(rival, on ? "ba:messagetype_rivalry_activated" : "ba:messagetype_rivalry_deactivated", true, pids);
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
            string rid = rd.id ?? "";
            lock (_lock) _planned.RemoveAll(p => p.RivalId == rid);
            Schedule($"surrender|{rid}", () =>
            {
                try
                {
                    var keys = new List<string>();
                    lock (_lock)
                        foreach (var kv in _store)
                            if (kv.Value.IsActive && kv.Key.EndsWith("|" + rid, System.StringComparison.Ordinal)) keys.Add(kv.Key.Substring(0, kv.Key.Length - rid.Length - 1));
                    foreach (var k in keys) DeliverOnce(k, rival, rival.timeline?.surrenderMessageKey, "surrender");
                    lock (_lock)
                        foreach (var kv in _store)
                            if (kv.Key.EndsWith("|" + rid, System.StringComparison.Ordinal)) kv.Value.IsActive = false;
                    Plugin.Logger.LogInfo($"[RivalAttn] rival {rid} surrenders (per-player path, {keys.Count} active key(s)) - the host runs DefeatRival once.");
                    RivalsHelper.DefeatRival(rd);
                    MPServer.PublishRivalStateIfChanged("rivalattn-surrender");
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalAttn] surrender {rid}: {ex.Message}"); }
            });
            return true;
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
                    lock (_lock) done = st == null || !st.IsActive || st.Completed.Contains(p.EntryId);
                    if (done) continue;
                    // PART A DRY RUN: nothing is fired and nothing is marked completed, so the next hourly check plans it again.
                    ModPathFirings++;
                    Plugin.Logger.LogInfo($"[RivalAttn] would fire {p.Mechanic} for {p.Key} on {p.RivalId} (entry {p.EntryId}; dry run - nothing fired, nothing completed).");
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
                        foreach (var pid in PidsOfStable(stable))
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
                        rows.Add($"{rival.rivalData.id}{{{nb}}}: native count={nc} income={ni:F0} reported={rq} active={(st?.isActive == true ? 1 : 0)} sent={st?.sentMessageKeys?.Count ?? 0} completed={st?.completedTimelineEntryIds?.Count ?? 0}");
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
