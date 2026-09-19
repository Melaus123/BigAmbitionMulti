using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Concurrent;
using UI.Load;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Runs on the host. Accepts client connections, maintains shared world state,
    /// and broadcasts updates to all peers.
    /// </summary>
    public static class MPServer
    {
        // Transport seam (Steam-connect slice 1): the host listens through
        // IHostTransport — LiteNetLib UDP today, + a Steam relay in slice 2.
        private static LnlHostTransport?  _transport;        // UDP (direct IP / LAN) — always on
        private static SteamHostTransport? _steamTransport;   // Steam relay — best-effort beside UDP

        /// <summary>Address key → player ID who owns it. Empty = unowned.
        /// CONCURRENT: written on the network poll thread (rent/disconnect) and
        /// read/iterated on the main thread (save manifest build, world snapshot).
        /// A plain Dictionary here corrupts under that race → coreclr access
        /// violation, so this must be a ConcurrentDictionary.</summary>
        public static readonly ConcurrentDictionary<string, string> BuildingOwners = new();
        /// <summary>addressKey → owner PlayerId (or "host") for BOUGHT real estate —
        /// distinct from BuildingOwners, which tracks who RENTS/operates.  The host is
        /// the single registrar of real-estate ownership, so two players can never own
        /// the same building.  Concurrent (written on the poll thread + the main thread,
        /// like BuildingOwners).</summary>
        public static readonly ConcurrentDictionary<string, string> BuildingRealEstateOwners = new();

        /// <summary>Ordered list of player IDs currently in the lobby (host is always
        /// index 0).  CONCURRENT: mutated under _lobbyLock on the poll thread (Hello /
        /// disconnect) and the main thread (host start / kick / mid-game join), and
        /// published as an immutable snapshot read locklessly by every consumer
        /// (copy-on-write).  Mutate only via the Lobby* helpers below.</summary>
        public static IReadOnlyList<string> LobbyPlayers => _lobbyPlayers;
        private static volatile List<string> _lobbyPlayers = new();
        private static readonly object _lobbyLock = new();
        private static void LobbyReset(string hostId) { lock (_lobbyLock) _lobbyPlayers = new List<string> { hostId }; }
        private static void LobbyClear() { lock (_lobbyLock) _lobbyPlayers = new List<string>(); }
        private static void LobbyAdd(string id)
        {
            lock (_lobbyLock)
            {
                if (_lobbyPlayers.Contains(id)) return;
                _lobbyPlayers = new List<string>(_lobbyPlayers) { id };
            }
        }
        private static void LobbyRemove(string id)
        {
            lock (_lobbyLock)
            {
                if (!_lobbyPlayers.Contains(id)) return;
                var n = new List<string>(_lobbyPlayers);
                n.Remove(id);
                _lobbyPlayers = n;
            }
        }

        /// <summary>True while waiting in the lobby; false once the game has been started.</summary>
        public static bool IsInLobby { get; private set; } = true;

        /// <summary>The MP save session the host picked in the "Host Saved Game" list
        /// (empty = load the newest).  Consumed by StartLoadGame.</summary>
        public static string ChosenLoadSession = "";

        /// <summary>Host setting: when true, the host's starting cash applies to everyone;
        /// when false, each client sets their own starting cash.</summary>
        public static bool EnforceStartingCash = true;

        /// <summary>Host lobby: per-player starting-cash overrides, keyed by playerId
        /// (the host's own included).  Absent ⇒ the player gets the difficulty/base
        /// StartingMoney.  Lets the host designate that specific players begin with
        /// more (or less) than the base.  Edited on the main thread (lobby UI),
        /// read on the main thread (StartNewGame) — no cross-thread access.</summary>
        public static readonly Dictionary<string, int> StartingCashByPlayer = new();

        /// <summary>Effective starting cash for a player: their override if set, else
        /// the supplied base.</summary>
        public static int StartingCashFor(string playerId, int baseCash)
            => (!string.IsNullOrEmpty(playerId) && StartingCashByPlayer.TryGetValue(playerId, out var v)) ? v : baseCash;

        /// <summary>Per-player self-chosen starting age (playerId → age, host included).
        /// Unlike cash, each player picks their OWN; the host just aggregates them for
        /// display + bakes each into that player's start settings.</summary>
        // CONCURRENT: written by the lobby UI (main thread) and the LobbyPref
        // handler (poll thread); copy-constructed during lobby broadcasts.
        public static readonly ConcurrentDictionary<string, int> StartingAgeByPlayer = new();

        public static int StartingAgeFor(string playerId, int baseAge)
        {
            int age = (!string.IsNullOrEmpty(playerId) && StartingAgeByPlayer.TryGetValue(playerId, out var v) && v > 0) ? v : baseAge;
            // Round-226 belt: whatever the source, never hand the game an age in its
            // zombie-walk (80+) or instant-death (90+) range.
            return Math.Max(16, Math.Min(79, age));
        }

        /// <summary>Records a player's chosen starting age and re-broadcasts the lobby
        /// so everyone sees it.  Called for the host's own age (from the lobby UI) and
        /// for each client's reported age (LobbyPref).</summary>
        public static void SetStartingAge(string playerId, int age)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            StartingAgeByPlayer[playerId] = age;
            if (IsInLobby) BroadcastLobbyUpdate();
        }

        // CONCURRENT: the connection registry is mutated on the poll thread (Hello
        // adds, disconnect removes) AND on the main thread (mid-game join approval
        // adds), and iterated on the main thread for every broadcast (~10 Hz).
        // Plain collections corrupt under that race — the same hazard documented
        // for BuildingOwners above — so both are concurrent.  _clients is a
        // ConcurrentDictionary used as a set; iterate it via .Keys.
        private static readonly ConcurrentDictionary<MPLink, byte> _clients   = new();
        private static readonly ConcurrentDictionary<int, string>   _peerNames = new(); // peer.Id → playerId
        /// <summary>Round-281: peer.Id → what BUILD that peer runs, bound once at Hello and dropped at
        /// disconnect.  Before this round the joiner's mod version was logged and thrown away (the
        /// "VERSION SKEW" warning below), so the host had no way to ask "can this peer parse message X"
        /// at send time.  Keyed by peer.Id because the interior subscriber sets are peer.Id sets.</summary>
        /// Round-283 adds `express` to the SAME record rather than opening a parallel dictionary:
        /// one registry means one place that binds at Hello and one place that drops at disconnect,
        /// so a future capability cannot be forgotten in half of them.
        private static readonly ConcurrentDictionary<int, (string version, bool cargoDelta, bool express)> _peerBuild = new();

        /// <summary>Round-281: may this peer be sent MessageType.InteriorCargoSync?  Two conditions,
        /// both required: the peer ANNOUNCED the capability (Hello.CargoDelta — absent on every older
        /// build, so it answers "is the handler present" exactly), and it runs our exact mod version
        /// (the conservative pairing: same code on both ends of a new channel).  An unknown peer — one
        /// whose Hello we never bound — is NOT capable; the answer defaults to "send the full snapshot",
        /// which is always correct, only larger.  `why` names the failing half for the send-site log.</summary>
        internal static bool IsDeltaCapablePeer(int peerId, out string why)
        {
            if (!_peerBuild.TryGetValue(peerId, out var b))
            {
                why = $"peer {peerId} has no recorded build (Hello not bound)";
                return false;
            }
            if (!b.cargoDelta)
            {
                why = $"peer {peerId} (v{(string.IsNullOrEmpty(b.version) ? "?" : b.version)}) did not announce cargo-delta support";
                return false;
            }
            if (b.version != MyPluginInfo.PLUGIN_VERSION)
            {
                why = $"peer {peerId} runs v{(string.IsNullOrEmpty(b.version) ? "?" : b.version)}, we run v{MyPluginInfo.PLUGIN_VERSION}";
                return false;
            }
            why = "";
            return true;
        }

        /// <summary>Round-283: may this peer be sent GameTimeSync on the EXPRESS lane?  Same two
        /// conditions and the same reasoning as IsDeltaCapablePeer above, but the risk it guards is
        /// different: an older peer PARSES an express clock packet perfectly well — what it lacks is
        /// the Seq freshness guard, so if an express packet overtakes an older one still stuck behind
        /// bulk, that older packet lands afterwards and can leave a perfectly aligned client wrongly
        /// AheadHeld for a cycle.  The flag proves the guard is present; version equality keeps the
        /// pairing conservative (round-281 precedent).  An unknown peer is NOT capable, and the
        /// answer "no" is always correct — it is exactly today's ordered send.</summary>
        internal static bool IsExpressCapablePeer(int peerId)
        {
            if (!_peerBuild.TryGetValue(peerId, out var b)) return false;
            return b.express && b.version == MyPluginInfo.PLUGIN_VERSION;
        }

        /// <summary>playerId → immutable StableId (for save/ownership persistence).
        /// Includes the host's own.  Populated from each client's Hello.
        /// CONCURRENT: written on the poll thread (Hello) + main thread (host
        /// start), read on the main thread (save/load) — must be thread-safe.</summary>
        public static readonly ConcurrentDictionary<string, string> StableIdByPlayer = new();

        /// <summary>stableId → last-known money (live-streamed from each player +
        /// the host's own).  The most-current cash figure to restore on reconnect,
        /// so a crash costs at most a few seconds of earnings.
        /// CONCURRENT: written on poll + main threads, read on the main thread.</summary>
        public static readonly ConcurrentDictionary<string, float> CashByStableId = new();

        /// <summary>pid → Environment.TickCount at their last lobby join (round-184 fix 3: the
        /// resurrection window — an essentially-empty owner push shortly after a (re)join is the
        /// stale/fresh-save signature and must not blank developed copies).  CONCURRENT (poll
        /// thread writes on Hello, main thread reads in the apply path).</summary>
        public static readonly ConcurrentDictionary<string, int> JoinedAtByPid = new();

        // ── Round-187c: throttled ownership-reject logging ───────────────────────
        // A stale-ownership desync (round-184 family) turns these rejects into a STORM —
        // 2,259 identical RegisterCashier warnings in one field log crowded everything else
        // out of the report's log budget.  The reject BEHAVIOR is unchanged; the log speaks
        // once per (kind, address) per 5 minutes and carries the suppressed count.
        private static readonly ConcurrentDictionary<string, (int nextMs, int suppressed)> _rejectLog = new();
        private static void LogRejectThrottled(string kind, string addr, string detail)
        {
            try
            {
                string key = kind + "|" + addr;
                int now = Environment.TickCount;
                var cur = _rejectLog.TryGetValue(key, out var v) ? v : (nextMs: 0, suppressed: 0);
                if (cur.nextMs != 0 && unchecked(now - cur.nextMs) < 0)
                {
                    _rejectLog[key] = (cur.nextMs, cur.suppressed + 1);
                    return;
                }
                string sup = cur.suppressed > 0 ? $" (+{cur.suppressed} suppressed in the last 5min)" : "";
                _rejectLog[key] = (now + 300_000, 0);
                Plugin.Logger.LogWarning($"[Server] {kind} for '{addr}' {detail} — dropped.{sup}");
            }
            catch { }
        }

        /// <summary>Last-synced cash for a player, or -1 when unknown (unknown
        /// must not block — the Hub treats negative as "can't validate").</summary>
        public static float GetKnownCash(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return -1f;
            string stable = StableIdByPlayer.TryGetValue(playerId, out var s) && !string.IsNullOrEmpty(s) ? s : playerId;
            return CashByStableId.TryGetValue(stable, out var m) ? m : -1f;
        }

        /// <summary>Record a player's latest cash, keyed by their stable id.</summary>
        public static void RecordCash(string playerId, float money)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            string stable = StableIdByPlayer.TryGetValue(playerId, out var s) && !string.IsNullOrEmpty(s) ? s : playerId;
            CashByStableId[stable] = money;
        }

        // ── Shared wallet (merger slice 4) — HOST ledger, one balance per merger group ────────────
        // groupId → authoritative balance. Deltas only (never absolutes); manifest-persisted with the
        // merger membership. MAIN THREAD (mutated from HostWalletDelta/HostMergerAction only).
        private static readonly Dictionary<string, float> _walletBalance = new();
        // groupId → stable ids whose merge-time wallet pooling was accepted (idempotency across
        // restore/join replays; removed on leave so a re-joiner pools their then-current wallet again).
        private static readonly Dictionary<string, HashSet<string>> _walletContributed = new();

        /// <summary>HOST, MAIN THREAD: a merged member reports a native money delta (or its one-time
        /// merge pooling). Ledger += delta, then the group hears the new truth.</summary>
        public static void HostWalletDelta(string pid, float amount, string key, bool contribution)
        {
            try
            {
                if (!_running) return;
                string stable = StableOfPid(pid);
                string g = MergerSync.GroupOfStable(stable);
                if (string.IsNullOrEmpty(g))
                {
                    Plugin.Logger.LogInfo($"[EconProbe] wallet delta {amount:N0} '{key}' from '{pid}' DROPPED (not in a merger — late message after leave?).");
                    return;
                }
                if (contribution)
                {
                    if (!_walletContributed.TryGetValue(g, out var set)) { set = new HashSet<string>(); _walletContributed[g] = set; }
                    if (!set.Add(stable))
                    {
                        Plugin.Logger.LogInfo($"[EconProbe] wallet POOL from '{pid}' ignored (already pooled — restore/join replay).");
                        return;
                    }
                }
                _walletBalance.TryGetValue(g, out var bal);
                _walletBalance[g] = bal + amount;
                Plugin.Logger.LogInfo($"[EconProbe] wallet ledger '{g}' {(amount >= 0 ? "+" : "")}{amount:N0} '{key}'{(contribution ? " (pool)" : "")} → ${_walletBalance[g]:N0}.");
                BroadcastWalletGroup(g);
                // ORDINARY deltas still write no manifest (they are frequent): the balance persists with
                // every coordinated save + on merger membership changes — the same staleness window as
                // the slot-cash mirrors it must stay consistent with.
                // WALLET-DUPE-1 (W1): a CONTRIBUTION is not an ordinary delta. The contributed-set is the
                // ONLY guard that stops a RESTORED roster pooling a member's wallet a second time, and it
                // reached the manifest only through RefreshGrantsAndBroadcast — which runs BETWEEN the
                // host's own pool and a remote member's, so a write landing in that window persisted a
                // HALF-FILLED set and the missing member pooled again on the next load (the double-money
                // class). Persist the set the instant a pool lands.
                if (contribution) MPSaveCoordinator.PersistGrantsNow();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Wallet] HostWalletDelta: {ex.Message}"); }
        }

        /// <summary>HOST: push one group's authoritative balance to everyone (receivers self-filter by
        /// group) and apply it locally when the host is a member of that group.</summary>
        public static void BroadcastWalletGroup(string groupId)
        {
            if (!_walletBalance.TryGetValue(groupId, out var bal)) return;
            var pay = new MergerWalletStatePayload { GroupId = groupId, Balance = bal };
            Broadcast(MessageEnvelope.Create(MessageType.MergerWalletState, "host", pay));
            MergerWallet.ApplyState(pay);   // host may be a member; ApplyState self-filters
        }

        /// <summary>HOST: every group's balance — join replay + the 10s merger heartbeat (lost-packet
        /// self-heal; also what snaps a freshly restored member's mirror to the manifest balance).</summary>
        public static void BroadcastAllWalletGroups()
        {
            foreach (var g in new List<string>(_walletBalance.Keys)) BroadcastWalletGroup(g);
        }

        /// <summary>Manifest snapshot accessors (MPSaveCoordinator).</summary>
        public static Dictionary<string, float> SnapshotWalletBalances() => new(_walletBalance);
        public static Dictionary<string, List<string>> SnapshotWalletContributed()
        {
            var d = new Dictionary<string, List<string>>();
            foreach (var kv in _walletContributed) d[kv.Key] = new List<string>(kv.Value);
            return d;
        }

        /// <summary>WALLET-DUPE-1 (W2): ONE group's authoritative balance, false when the host holds none.
        /// The single read MPSaveCoordinator.SavedCashFor needs — the ledger itself stays private.</summary>
        public static bool TryWalletBalance(string groupId, out float balance)
            => _walletBalance.TryGetValue(groupId ?? "", out balance);

        /// <summary>FOLD b X3 (r1 F3): has THIS process established what the merger state is? False from the
        /// moment a world begins until the manifest restore finishes; true afterwards, and true the instant a
        /// company is FORMED, joined, left or dissolved. Read by MPSaveCoordinator.PersistGrantsNow: its
        /// "keep whatever the manifest already holds when the live store is empty" guard exists only to survive
        /// the gap before the restore, so once this is true an EMPTY live store is the truth and must be written
        /// through — otherwise a dissolved company survives on disk and a later load resurrects it.</summary>
        public static bool MergerStateAuthoritative { get; private set; }

        /// <summary>The live merger/wallet stores now say what is true (restore finished, or a form/leave/dissolve
        /// just changed them). One-way within a world; cleared only at a world boundary.</summary>
        internal static void MarkMergerStateAuthoritative() => MergerStateAuthoritative = true;

        /// <summary>HOST: restore the wallet ledger from a manifest (clear-then-apply, beside the
        /// merger store restore).</summary>
        public static void RestoreWalletFromManifest(MpManifest m)
        {
            _walletBalance.Clear(); _walletContributed.Clear();
            if (m.MergerWalletBalance != null)
                foreach (var kv in m.MergerWalletBalance) _walletBalance[kv.Key] = kv.Value;
            if (m.MergerWalletContributed != null)
                foreach (var kv in m.MergerWalletContributed) _walletContributed[kv.Key] = new HashSet<string>(kv.Value ?? new List<string>());
            // FOLD b X4 (r1 F4): the contributed-set is what stops the membership rising edge pooling a wallet
            // twice, and restoring it from the manifest ALONE left a hole — a roster member the manifest never
            // listed as contributed pooled its RESTORED personal cash on the edge, and that cash is a SHARE of a
            // balance the restored figure already contains. Structural close: a restored group's balance is by
            // definition the whole company, so EVERY member of that restored roster has already contributed to
            // it. The merger store restore runs before this call (RestoreOwnershipFromManifest: StoreRestore
            // :~427, this :~461), so MergerSync is the roster of record here.
            int marked = 0;
            foreach (var g in new List<string>(_walletBalance.Keys))
            {
                if (!MergerSync.StoreGroups.TryGetValue(g, out var roster) || roster == null) continue;
                if (!_walletContributed.TryGetValue(g, out var set)) { set = new HashSet<string>(); _walletContributed[g] = set; }
                foreach (var stable in roster) if (!string.IsNullOrEmpty(stable) && set.Add(stable)) marked++;
            }
            if (marked > 0)
                Plugin.Logger.LogInfo($"[EconProbe] wallet restore: {marked} member(s) marked contributed from the roster.");
            if (_walletBalance.Count > 0)
                Plugin.Logger.LogInfo($"[EconProbe] wallet restored {_walletBalance.Count} group balance(s) from manifest.");
        }

        /// <summary>World boundary: the ledger goes, and with it this process's claim to know the merger state
        /// (X3) — the next world must re-earn it through a manifest restore or a fresh FORM.</summary>
        public static void ResetWallet() { _walletBalance.Clear(); _walletContributed.Clear(); MergerStateAuthoritative = false; }

        /// <summary>Host: rebuild the live ownership map from a session manifest
        /// (re-keying the stableId-keyed owners back to the live playerIds of
        /// connected players; absent owners stay reserved under their stableId
        /// until they reconnect) and seed last-known cash.  Phase 4 load.</summary>
        public static void RestoreOwnershipFromManifest(MpManifest m)
        {
            try
            {
                // 2026-09-05 colours: the permanent slot table rides the manifest, seeded before any join can assign.
                PlayerColours.HostSeed(m.ColourSlots ?? new Dictionary<string, int>());
                PlayerColours.Learn(MPConfig.PlayerId, PlayerColours.HostAssign(MPConfig.StableId));   // the host keeps its own slot across the load
                // colours r3 (review r2 MAJOR-1): anyone who said Hello in the LOBBY was assigned against the pre-load table; re-assign
                // every known player against the seeded one so lobby joiners keep a persisted, non-duplicated slot.
                foreach (var kv in StableIdByPlayer.OrderBy(k => k.Value, StringComparer.Ordinal))   // colours r4 (review r3 MINOR-1): deterministic slot order for unseeded lobby joiners
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                        PlayerColours.Learn(kv.Key, PlayerColours.HostAssign(kv.Value));
                var reverse = new Dictionary<string, string>();   // stableId → playerId
                foreach (var kv in StableIdByPlayer) reverse[kv.Value] = kv.Key;

                int priorOwned = BuildingOwners.Count;   // [Ledger] shrink probe (round-184)
                BuildingOwners.Clear();
                foreach (var kv in m.BuildingOwners)
                {
                    string ownerStable = kv.Value;
                    if (string.IsNullOrEmpty(ownerStable)) continue;
                    if (ownerStable == MPConfig.StableId)             BuildingOwners[kv.Key] = "host";
                    else if (reverse.TryGetValue(ownerStable, out var pid)) BuildingOwners[kv.Key] = pid;
                    else                                              BuildingOwners[kv.Key] = ownerStable; // reserved (absent owner)
                }

                BuildingRealEstateOwners.Clear();
                foreach (var kv in m.BuildingRealEstateOwners)
                {
                    string ownerStable = kv.Value;
                    if (string.IsNullOrEmpty(ownerStable)) continue;
                    if (ownerStable == MPConfig.StableId)             BuildingRealEstateOwners[kv.Key] = "host";
                    else if (reverse.TryGetValue(ownerStable, out var pid2)) BuildingRealEstateOwners[kv.Key] = pid2;
                    else                                              BuildingRealEstateOwners[kv.Key] = ownerStable; // reserved (absent owner)
                }
                foreach (var slot in m.Slots)
                    if (slot.Money != 0f) CashByStableId[slot.StableId] = slot.Money;

                Plugin.Logger.LogInfo($"[Server] Restored {BuildingOwners.Count} owned building(s) from manifest ({m.Slots?.Count ?? 0} save slot(s)).");
                // [Ledger] shrink probe (round-184, field bamp-bug-20260729-150703): a load that
                // yields fewer entries than the ledger held a moment ago is a SILENT ownership
                // rollback — the exact shape suspected of eating Curlingg's businesses.  Loud line
                // so both dev tests and field reports name the shrink moment.
                if (priorOwned > 0 && BuildingOwners.Count < priorOwned)
                    Plugin.Logger.LogWarning($"[Ledger] RESTORE SHRANK the ownership ledger: {priorOwned} → {BuildingOwners.Count} entries — this manifest is older/more partial than the world state just left behind (probe, round-184).");

                // 4a diagnostic (read-only): a building owned by a stableId with NO save slot is an ORPHAN —
                // the owner has no character on the host (legacy save, or a lost/corrupt .hsg). Surfaced at
                // WARN so it shows up in a submitted bug report instead of silently locking a building to a
                // ghost. Does NOT touch the data — diagnosis only.
                var slotIds = new HashSet<string>();
                if (m.Slots != null) foreach (var s in m.Slots) if (!string.IsNullOrEmpty(s.StableId)) slotIds.Add(s.StableId);
                int orphans = 0;
                foreach (var kv in m.BuildingOwners)
                {
                    string owner = kv.Value;
                    if (string.IsNullOrEmpty(owner) || owner == MPConfig.StableId) continue;   // host-owned is fine
                    if (!slotIds.Contains(owner))
                    {
                        orphans++;
                        Plugin.Logger.LogWarning($"[Server] ORPHAN ownership: building '{kv.Key}' owned by '{owner}' with NO save slot this session — owner character missing (legacy/lost save?).");
                    }
                }
                if (orphans > 0)
                    Plugin.Logger.LogWarning($"[Server] {orphans} orphan-owned building(s) on load (owner has no save). Not auto-changed — diagnostic only.");

                // Access grants ("keys") — StableId-keyed, so no re-keying needed (the runtime
                // PlayerId table is rebuilt from this on every join). CLEAR-then-apply, and refresh + log
                // UNCONDITIONALLY: a save with no grants must still flush whatever a previously-loaded
                // session left in the store (cross-session leak guard), and a "0 grant(s)" line at load is
                // the definitive "this save carries none" signal — the old silent skip is exactly what made
                // the shares-lost-on-load rounds ambiguous (2026-06-30).
                GrantSync.ResetStore();
                int gn = 0;
                if (m.Grants != null)
                    foreach (var g in m.Grants)
                    {
                        if (string.IsNullOrEmpty(g.Owner) || string.IsNullOrEmpty(g.Grantee)) continue;
                        GrantSync.StoreSet(g.Kind, g.Owner, g.Grantee, true);
                        if (!string.IsNullOrEmpty(g.GranteeName)) GrantSync.NoteName(g.Grantee, g.GranteeName);
                        gn++;
                    }
                // Merger membership — same clear-then-apply + unconditional log discipline as grants.
                MergerSync.ResetStore();
                int mn = 0;
                if (m.Merger != null)
                {
                    // Phase 1-A: restore in the stored JOIN ORDER (Order = -1 on an older manifest, where
                    // file order is the order) — a stable index tie-break keeps that file order intact.
                    var seq = new List<int>();
                    for (int i = 0; i < m.Merger.Count; i++) seq.Add(i);
                    seq.Sort((x, y) =>
                    {
                        int c = (m.Merger[x]?.Order ?? -1).CompareTo(m.Merger[y]?.Order ?? -1);
                        return c != 0 ? c : x.CompareTo(y);
                    });
                    foreach (var i in seq)
                    {
                        var mem = m.Merger[i];
                        if (string.IsNullOrEmpty(mem?.StableId)) continue;
                        // Old manifests carry no Group — fold them into one legacy group.
                        MergerSync.StoreRestore(string.IsNullOrEmpty(mem.Group) ? "legacy" : mem.Group, mem.StableId, mem.GroupSeq);
                        if (!string.IsNullOrEmpty(mem.Name)) GrantSync.NoteName(mem.StableId, mem.Name);
                        mn++;
                    }
                }
                // Merger phase 3-A: the paperwork store belongs to THIS manifest's moment - replaced
                // from the slot being restored, beside the merger roster, clear-then-apply. An older
                // slot can never keep the newer world's bundles (user rule 2026-09-11).
                RestorePaperworkFromManifest(m);
                // Phase 4a (G1): the books store follows the paperwork store exactly - same moment, same
                // clear-then-apply, same timeline rule. AFTER the paperwork restore, whose ResetPaperwork
                // already cleared the books store once (this re-clear is idempotent and keeps the contract
                // local to the call that applies).
                RestoreCompanyBooksFromManifest(m);
                // P3-B: the absence marks follow the same timeline - clear-then-apply from THIS
                // manifest, BEFORE HostReconcileAbsence can run, so a restored mark keeps its SinceDay
                // and the reconcile only re-designates the simulator. This machine's own simulation
                // state dies with the world either way.
                MergerAbsence.HostReset();
                MergerAbsence.Reset();
                RestoreAbsenceFromManifest(m);
                // Phase 4b (people) P2/P1 r2: the in-transit transfers follow the absence marks exactly -
                // same moment, same clear-then-apply. The candidate pool and its claims are a SESSION
                // thing and are simply cleared: the members republish theirs within one tick of the load.
                RestoreTransfersFromManifest(m);
                RestoreCargoTransfersFromManifest(m);   // 4c part 2: cargo in transit rides the same moment and the same rule
                // 4c part 2 r2 (F6a): this machine's own cargo statics are per WORLD - _started was never
                // cleared, so loading back to the same day and hour left every id of that hour "already
                // started" and the leg silently did nothing. Cleared here, then the PERSISTED idempotence
                // marks (F3 closed ids / F6c applied ids) are read back for the world being loaded.
                MPSaveCoordinator.RestoreCargoMarksNow(m);
                HostResetCandidates();
                try { PaperworkSync.Reset(); } catch { }   // and this machine's publisher forgets the previous world's day/edge
                PruneOffers("session state restored");   // r4: the restored store decides which offers still stand
                RestoreWalletFromManifest(m);   // slice 4: ledger BEFORE the broadcast below (members snap to it)
                MarkMergerStateAuthoritative(); // X3: store + wallet are both restored — an empty live store now MEANS empty
                MPHub.RestoreLoans(m.Loans);    // sweep 2026-08-18: the loaded slot's loans are the timeline truth
                RefreshGrantsAndBroadcast();
                Plugin.Logger.LogInfo($"[Server] Restored {gn} access grant(s) + {mn} merger member(s) from manifest ({(m.Grants?.Count ?? 0)} grants in file).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] RestoreOwnershipFromManifest: {ex.Message}"); }
        }

        // ══ MERGER PHASE 3-A - PAPERWORK STORE (2026-09-11, plan §9, D13) ═══════════════════════
        // The host keeps the LATEST paperwork bundle per member (latest wins, with the sender's game
        // day beside it) and copies the store into the manifest MODEL (MpManifest.Paperwork) at every
        // manifest write for the session it is RUNNING - never into a .hsg, and never into any native
        // save field. The store follows the save timeline exactly: replaced from the loaded slot's
        // manifest on every load, never carried over in memory (user rule 2026-09-11). P3-B reads
        // PaperworkStore to hand an absent member's businesses to a simulator. This build only stores.

        /// <summary>One member's stored bundle. Json is the serialised BusinessPaperworkPayload -
        /// kept as text so the manifest section is a straight passthrough and a future payload shape
        /// round-trips through an older host untouched.</summary>
        public class PaperworkEntry
        {
            public string StableId    { get; set; } = "";
            public int    Day         { get; set; }       // the sender's game day at publish time
            public int    ReceivedDay { get; set; }       // the HOST's game day when it landed
            public string Json        { get; set; } = "";
        }

        private static readonly Dictionary<string, PaperworkEntry> _paperwork = new();

        /// <summary>StableId to that member's latest paperwork bundle (P3-B's read surface).</summary>
        public static IReadOnlyDictionary<string, PaperworkEntry> PaperworkStore => _paperwork;

        public static void ResetPaperwork()
        {
            try { CompanyBooks.HostReset(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] host store reset: {ex.Message}"); }   // phase 4a rides the same world boundary
            try { CompanyFeed.HostReset(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] host ring reset: {ex.Message}"); }      // phase 4b: the feed ring is memory-only and rides the same boundary
            lock (_paperwork) _paperwork.Clear();
            ResetPlanEditSeen();      // 4c part 2a r2 MAJOR-2: the plan-edit duplicate table is per session too
            lock (_capWarnedDay) _capWarnedDay.Clear();
            lock (_resendServedAt) { _resendServedAt.Clear(); _resendThrottleLogged.Clear(); }   // r7: the throttle dies with the session too
        }

        /// <summary>HOST (r7): hand the parts of `addrs` BACK from the discarded owner copy to the sender's
        /// bundle. MergeAddresses MOVES (Surgery empties its source), so after the filing merge the fresh
        /// parts live only in `owned` - they are re-split from there. The sender's own copy of those
        /// addresses was moved out earlier, so nothing is duplicated: the bundle ends exactly as it arrived.</summary>
        private static void GiveBack(BusinessPaperworkPayload sender, BusinessPaperworkPayload owned, HashSet<string> addrs)
        {
            try
            {
                var back = PaperworkSync.SplitOutAddresses(owned, addrs, out int n);
                if (n > 0) PaperworkSync.MergeAddresses(sender, back, addrs);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] give-back: {ex.Message}"); }
        }

        /// <summary>HOST (r6 F2): true at most ONCE per owner per game day, so an entry that sits over the
        /// 2 MB cap warns once instead of at every publish.</summary>
        private static readonly Dictionary<string, int> _capWarnedDay = new();
        private static bool CapWarnDue(string ownerStable, int day)
        {
            string k = ownerStable ?? "";
            lock (_capWarnedDay)
            {
                if (_capWarnedDay.TryGetValue(k, out var d) && d == day) return false;
                _capWarnedDay[k] = day;
                return true;
            }
        }

        /// <summary>Host: take one member's bundle. Called from the receive case (a validated sender)
        /// and directly by PaperworkSync when the HOST itself is the member.</summary>
        public static void StorePaperwork(BusinessPaperworkPayload p, string senderPid)
        {
            try
            {
                if (p == null) return;
                string json;
                try { json = Newtonsoft.Json.JsonConvert.SerializeObject(p); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] store serialise for '{senderPid}': {ex.Message}"); return; }
                int bytes = System.Text.Encoding.UTF8.GetByteCount(json);
                if (bytes > PaperworkSync.MaxBundleBytes)
                {
                    Plugin.Logger.LogWarning($"[Paperwork] REFUSED bundle from '{senderPid}': {bytes} bytes > {PaperworkSync.MaxBundleBytes} cap - the previous bundle stands.");
                    return;
                }
                // The durable key is the StableId (survives renames and offline members, like grants
                // and the merger roster). A sender that somehow has none falls back to its pid.
                string key = string.IsNullOrEmpty(p.StableId) ? (senderPid ?? "") : p.StableId;
                if (string.IsNullOrEmpty(key)) return;
                // P3-B r4 C2/F2: a SIMULATOR publishes the absent owner's shops inside its OWN bundle
                // (PaperworkSync.Build's SimulatesHere gate), and filing that under the SENDER froze the
                // owner's entry at the moment they dropped. Every re-send of the hand-over then
                // re-installed that STALE copy - nextDeliveryDay, daysUntilRepeat, plan nextUpdateDay
                // and paidLicensingFeesToday all rewound, so deliveries re-fired and the POOLED WALLET
                // paid again. File those addresses under the OWNER first; what is left is the sender's.
                var filedOwners = new List<string>();
                try
                {
                    _lastFiledOwners.Clear();
                    if (HostFileSimulatedPaperwork(p, senderPid))
                    {
                        json  = Newtonsoft.Json.JsonConvert.SerializeObject(p);
                        bytes = System.Text.Encoding.UTF8.GetByteCount(json);
                    }
                    filedOwners.AddRange(_lastFiledOwners);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] owner filing for '{senderPid}': {ex.Message}"); }
                int hostDay = 0; try { hostDay = GameStateReader.GetGameTime().day; } catch { }
                // P3-B r6 F1: a RETURNED owner's first publish is its PRE-ABSENCE view of the shops the
                // simulator ran while it was away, and r5 stops that simulator the moment the owner is back
                // (its installs lifted) - so the host's entry is the ONLY structured record of the absence
                // period, and a wholesale store wiped it within 30 s (PaperworkSync's publish cadence),
                // leaving the mark pointing at nothing simulated. While the mark says OwnerBack the host
                // KEEPS its own parts for the MARKED addresses and takes the publisher's for every OTHER
                // address. P3-C clears the mark - at the returned owner's ACK (r2 F4), not at the send -
                // and that is what ends this guard. The ack precedes the owner's next publish, so the
                // guard is always gone by the time that publish arrives.
                int keptAddrs = 0, keptParts = 0;
                try
                {
                    if (MergerAbsence.Marks.TryGetValue(key, out var back) && back != null
                        && back.OwnerBack && back.Addresses.Count > 0)
                    {
                        string had;
                        lock (_paperwork) had = _paperwork.TryGetValue(key, out var pe) ? (pe.Json ?? "") : "";
                        var marked = new HashSet<string>(back.Addresses, StringComparer.OrdinalIgnoreCase);
                        marked.Remove("");
                        if (!string.IsNullOrEmpty(had) && marked.Count > 0)
                        {
                            var hostPw = Newtonsoft.Json.JsonConvert.DeserializeObject<BusinessPaperworkPayload>(had);
                            if (hostPw != null)
                            {
                                // The host's simulated parts for exactly those addresses go back over the
                                // publisher's copy of them (MergeAddresses drops the incoming ones first).
                                var simulated = PaperworkSync.SplitOutAddresses(hostPw, marked, out int simParts);
                                if (simParts > 0)
                                {
                                    int simBiz = simulated.Businesses?.Count ?? 0;   // r7: counted BEFORE the merge empties `simulated`
                                    PaperworkSync.MergeAddresses(p, simulated, marked);
                                    string gjson = Newtonsoft.Json.JsonConvert.SerializeObject(p);
                                    int gbytes = System.Text.Encoding.UTF8.GetByteCount(gjson);
                                    if (gbytes > PaperworkSync.MaxBundleBytes)
                                    {
                                        // Never store over the cap, never drop the simulated record: the
                                        // owner's previous entry stands until P3-C consumes it.
                                        if (CapWarnDue(key, hostDay))
                                            Plugin.Logger.LogWarning($"[Paperwork] merged entry for returned owner '{senderPid}' would be "
                                                                   + $"{gbytes} bytes > {PaperworkSync.MaxBundleBytes} cap - the previous entry stands.");
                                        return;
                                    }
                                    keptAddrs = simBiz; keptParts = simParts;   // r7: what the host's entry actually held, not the mark's size
                                    json  = gjson;
                                    bytes = gbytes;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] return guard for '{senderPid}': {ex.Message}"); }
                if (keptParts > 0)
                    Plugin.Logger.LogInfo($"[Paperwork] kept the simulated record of {keptAddrs} businesses ({keptParts} parts) for "
                                        + $"returned owner '{senderPid}' (return leg pending).");
                lock (_paperwork)
                    _paperwork[key] = new PaperworkEntry { StableId = key, Day = p.Day, ReceivedDay = hostDay, Json = json };
                Plugin.Logger.LogInfo($"[Paperwork] stored for '{senderPid}' (day {p.Day}, {bytes} bytes).");
                // MERGER PHASE 2 WAVE 4 (V1, D18): the DISPLAY-COPY fan-out rides the D14 books hook's twin -
                // every STORE of an owner's paperwork. This is the only event that carries the owner's live
                // agreement lists, so there is nothing earlier to hook. Owner-filed parts (the simulator
                // case above) are fanned out under the OWNER, not the sender, by FanOutCompanyLists(key).
                FanOutCompanyLists(key, "paperwork stored");
                foreach (var ok in filedOwners) if (ok != key) FanOutCompanyLists(ok, "paperwork filed under its owner");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] store: {ex.Message}"); }
        }

        /// <summary>HOST, MAIN THREAD (P3-B r4 F2): split one incoming bundle by OWNER. Every address in
        /// it that THIS host has marked as simulated BY THIS SENDER belongs to the absent owner of that
        /// mark: that address's business record, its owner-list items and its employee records are lifted
        /// out of the sender's bundle and merged into the OWNER's stored entry, replacing only those
        /// addresses' parts there and leaving the owner's other addresses exactly as they were. The store
        /// stays TEXT - the owner's entry is deserialised, merged and re-serialised - so the manifest
        /// section is still the straight passthrough P3-A designed and no other reader changes. Returns
        /// true when anything moved (the caller then re-serialises what is left of the sender's).</summary>
        private static bool HostFileSimulatedPaperwork(BusinessPaperworkPayload p, string senderPid)
        {
            if (p == null || string.IsNullOrEmpty(senderPid) || MergerAbsence.MarkCount == 0) return false;
            var mine = new List<MergerAbsence.AbsenceMark>();
            foreach (var kv in MergerAbsence.Marks)
                if (kv.Value != null && kv.Value.SimulatorPid == senderPid && kv.Value.Addresses.Count > 0)
                    mine.Add(kv.Value);
            if (mine.Count == 0) return false;

            bool any = false;
            int hostDay = 0; try { hostDay = GameStateReader.GetGameTime().day; } catch { }
            foreach (var mark in mine)
            {
                var addrs = new HashSet<string>(mark.Addresses, StringComparer.OrdinalIgnoreCase);
                addrs.Remove("");
                if (addrs.Count == 0) continue;
                var moved = PaperworkSync.SplitOutAddresses(p, addrs, out int parts);
                if (parts == 0) continue;

                string have;
                lock (_paperwork) have = _paperwork.TryGetValue(mark.OwnerStable, out var pe) ? (pe.Json ?? "") : "";
                BusinessPaperworkPayload? owned = null;
                if (!string.IsNullOrEmpty(have))
                {
                    try { owned = Newtonsoft.Json.JsonConvert.DeserializeObject<BusinessPaperworkPayload>(have); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] owner entry parse '{mark.OwnerStable}': {ex.Message}"); }
                }
                owned ??= new BusinessPaperworkPayload();
                if (!string.IsNullOrEmpty(mark.OwnerPid)) owned.PlayerId = mark.OwnerPid;
                owned.StableId = mark.OwnerStable;
                if (p.Day > owned.Day) owned.Day = p.Day;
                int movedBiz = moved.Businesses?.Count ?? 0;   // r7: counted BEFORE the merge - MergeAddresses MOVES (it empties `moved`)
                PaperworkSync.MergeAddresses(owned, moved, addrs);

                string mjson;
                try { mjson = Newtonsoft.Json.JsonConvert.SerializeObject(owned); }
                catch (Exception ex)
                {
                    // r6 F2: SplitOutAddresses already TOOK these parts out of the sender's bundle, so a
                    // bare 'continue' lost them from BOTH entries. Put them back before giving up - r7: from
                    // `owned`, which is where the merge above MOVED them (`moved` is empty by now).
                    Plugin.Logger.LogWarning($"[Paperwork] owner entry serialise '{mark.OwnerStable}': {ex.Message}");
                    GiveBack(p, owned, addrs);
                    continue;
                }
                int mbytes = System.Text.Encoding.UTF8.GetByteCount(mjson);
                if (mbytes > PaperworkSync.MaxBundleBytes)
                {
                    // r6 F2 (m-a): the same loss, and this one RECURRED at every publish while over the cap.
                    // The moved parts go BACK into the sender's bundle, so they live in exactly one entry
                    // instead of none; the WARN is once per owner per game day. r7: taken back from `owned`
                    // (the merge above MOVED them there; `moved` is empty by now).
                    GiveBack(p, owned, addrs);
                    if (CapWarnDue(mark.OwnerStable, hostDay))
                        Plugin.Logger.LogWarning($"[Paperwork] REFUSED merged entry for '{mark.OwnerPid}': {mbytes} bytes > "
                                               + $"{PaperworkSync.MaxBundleBytes} cap - that owner's previous entry stands "
                                               + $"and the {parts} moved parts stay with '{senderPid}'.");
                    continue;
                }
                lock (_paperwork)
                    _paperwork[mark.OwnerStable] = new PaperworkEntry
                    { StableId = mark.OwnerStable, Day = owned.Day, ReceivedDay = hostDay, Json = mjson };
                any = true;
                _lastFiledOwners.Add(mark.OwnerStable);   // wave 4: this OWNER's display copies changed too
                Plugin.Logger.LogInfo($"[Paperwork] filed {movedBiz} simulated businesses of "
                                    + $"'{mark.OwnerPid}' from '{senderPid}'.");
            }
            return any;
        }

        /// <summary>A one-line census for the TestDrive verb: "stable=<id>:day/bytes,...". The store is
        /// keyed by StableId, so the census says so in the format (review r1 MINOR).</summary>
        public static string PaperworkCensus(string filterStableId)
        {
            var sb = new System.Text.StringBuilder();
            lock (_paperwork)
                foreach (var kv in _paperwork)
                {
                    if (!string.IsNullOrEmpty(filterStableId) && kv.Key != filterStableId) continue;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append("stable=").Append(kv.Key).Append(':').Append(kv.Value.Day).Append('/')
                      .Append(System.Text.Encoding.UTF8.GetByteCount(kv.Value.Json ?? ""));
                }
            return sb.ToString();
        }

        /// <summary>Host: the store as manifest MODEL entries, copied into MpManifest.Paperwork at
        /// every manifest write for the session this host is RUNNING. The bundles ride the model like
        /// the merger roster and the loan ledger, so they inherit WriteManifest's atomic temp +
        /// File.Replace and there is no second write to tear, lose, or re-apply.</summary>
        /// <summary>P3-B: one member's stored bundle as TEXT for the hand-over ("" when the host has
        /// none - a member who never published still gets its interiors and its marks).</summary>
        public static string PaperworkJsonForStable(string stable)
        {
            if (string.IsNullOrEmpty(stable)) return "";
            lock (_paperwork) return _paperwork.TryGetValue(stable, out var e) ? (e.Json ?? "") : "";
        }

        // ══ MERGER PHASE 3-B - DESIGNATION (2026-09-11, plan §9, D1/D13/D15) ══════════════════

        /// <summary>HOST, MAIN THREAD: the ONE absence validator. Every membership change, roster
        /// change and departure reaches it through RefreshGrantsAndBroadcast (the 10 s merger-state
        /// cadence is the safety net), so there is no separate departure timer and no path that can
        /// leave a stale designation:
        ///   • a company member who is OFFLINE while a co-member is online gets a MARK naming a
        ///     SIMULATOR - the host when the host is a member of that company, else the member online
        ///     the LONGEST (JoinedAtByPid, the connect stamp bound at Hello), falling back to the
        ///     first online member in join order when no stamp exists (D1);
        ///   • a mark whose simulator has itself dropped is simply re-pointed and re-sent - the host
        ///     holds every push, so the new simulator is seeded exactly like the first (D15);
        ///   • a mark whose company dissolved or whose owner left it is DROPPED and the old simulator
        ///     told to undo (B5) - but NOT one whose owner is merely BACK (r4 m1, below);
        ///   • a company with NOBODY online simulates nothing (D1), but its existing marks are
        ///     SUSPENDED (SimulatorPid cleared), never dropped - a host restart with the whole company
        ///     offline must not lose the SinceDay the return leg reads (P3-B/R1);
        ///   • an owner who RETURNS is logged once WITH that SinceDay (B4) and the mark is KEPT and
        ///     flagged OwnerBack (r4 m1 - it used to fall straight into the sweep below in the same
        ///     pass, so the return leg would have found nothing to copy); the owner's own machine is
        ///     never told to un-flip anything, and the copy-back off the simulator's tagged installs is
        ///     P3-C, whose owner-side ACK is what CLEARS the mark (r2 F4). r5: the simulator is told to STOP the moment the
        ///     owner is back (one machine per address, always), so what P3-C consumes is the HOST's stored
        ///     record of the absence - which StorePaperwork's OwnerBack guard (r6 F1) keeps the returned
        ///     owner's own republish from overwriting.
        /// Idempotent: nothing is sent unless the designation actually changed - and when it DOES
        /// change, the OLD simulator is told to stop BEFORE the new one is handed the same owner
        /// (P3-B r1 MAJOR-1; without that both machines ran the same shops).</summary>
        internal static void HostReconcileAbsence()
        {
            if (!_running) return;
            try
            {
                // r1 m2: a session with no merger and no marks reconciles nothing - no day read, no HashSet.
                if (MergerSync.StoreGroups.Count == 0 && MergerAbsence.MarkCount == 0) return;
                int day = 0; try { day = GameStateReader.GetGameTime().day; } catch { }
                var wanted = new HashSet<string>();

                foreach (var grp in MergerSync.StoreGroups)
                {
                    var online = PidsOfGroup(grp.Key);          // join order, ONLINE only
                    if (online.Count == 0)
                    {
                        // D1 + P3-B/R1: nobody online, so nothing simulates and no NEW mark is made
                        // here. An EXISTING mark (restored from the manifest, or left behind when the
                        // whole company went dark) PERSISTS with its SimulatorPid CLEARED - dropping it
                        // would throw away the SinceDay the return leg needs. It is spared the dead
                        // sweep below and re-designated the moment any member of the company is back.
                        foreach (var stable in MergerSync.JoinOrderOfGroup(grp.Key))
                            if (MergerAbsence.HostSuspendMark(stable)) wanted.Add(stable);
                        continue;
                    }
                    string sim = online.Contains(MPConfig.PlayerId) ? MPConfig.PlayerId : LongestOnlineOf(online);
                    if (string.IsNullOrEmpty(sim)) continue;

                    foreach (var stable in MergerSync.JoinOrderOfGroup(grp.Key))
                    {
                        string pid = PidOfStable(stable);
                        if (IsOnlinePid(pid))
                        {
                            // B4 + r4 m1: log once, flag OwnerBack, and WANT the mark. Without the
                            // wanted.Add the dead sweep below dropped it in this very pass.
                            if (MergerAbsence.HostNoteReturn(stable, pid, day)) wanted.Add(stable);
                            // P3-C r2 (F4): a return has gone out for this mark and the owner has not
                            // acked it yet. The mark lives until that ack, so this pass must neither
                            // re-send the return nor re-designate a simulator - it just keeps the mark
                            // wanted (the ack clears it; an owner who drops again resets the flag below).
                            if (MergerAbsence.IsReturnSent(stable)) { wanted.Add(stable); continue; }
                            // P3-C (C1) RECURRENCE: the applying edge that normally fires the return can
                            // PRECEDE the flag above (the peer reports Settled before this pass flips
                            // OwnerBack), and nothing else would ever come back for that mark. This 10 s
                            // pass catches that ordering - and only once the peer is provably past its own
                            // load, so the return still overwrites the load instead of the other way round.
                            // No broadcast from here: the caller broadcasts the state right after this
                            // reconcile, and it will already lack the mark this cleared.
                            if (IsPlayerApplying(pid)) HostReturnIfMarked(pid, broadcast: false);
                            continue;
                        }
                        MergerAbsence.HostClearReturnLog(pid);
                        MergerAbsence.HostClearReturnSent(stable);   // r2 F4: absent again - a sent return is void
                        if (sim == pid) continue;                             // cannot simulate for itself
                        var addrs = AddressesOfStable(stable);
                        if (addrs.Count == 0) continue;                       // nothing to hand over
                        wanted.Add(stable);
                        // r1 MAJOR-1: who WAS simulating, read BEFORE HostSetMark over-writes the mark.
                        // A changed SimulatorPid is a RE-DESIGNATION: the old machine has to give the
                        // veil exception, the promoted staff and the installed paperwork back first.
                        string wasSim = MergerAbsence.Marks.TryGetValue(stable, out var had) ? had.SimulatorPid : "";
                        var wasAddrs = had != null ? new List<string>(had.Addresses) : new List<string>();
                        if (MergerAbsence.HostSetMark(stable, pid, sim, addrs, day))
                        {
                            if (!string.IsNullOrEmpty(wasSim) && wasSim != sim)
                                MergerAbsence.HostSendDrop(wasSim, stable, pid, wasAddrs, $"re-designated to '{sim}'");
                            if (MergerAbsence.Marks.TryGetValue(stable, out var mk)) MergerAbsence.SendHandover(mk);
                        }
                        else if (sim == MPConfig.PlayerId && !MergerAbsence.SimulatesHere(addrs[0])
                                 && MergerAbsence.Marks.TryGetValue(stable, out var mk2))
                            MergerAbsence.SendHandover(mk2);                  // scene churn wiped the local apply
                    }
                }

                var dead = new List<string>();
                foreach (var kv in MergerAbsence.Marks) if (!wanted.Contains(kv.Key)) dead.Add(kv.Key);
                foreach (var s in dead)
                    MergerAbsence.HostDropMark(s, "company dissolved, owner no longer a member, owner is back online, or no addresses left");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] reconcile: {ex.Message}"); }
        }

        /// <summary>HOST, MAIN THREAD (MERGER PHASE 3-C, C1): the RETURN LEG's trigger. For every mark
        /// that says this peer is the OWNER and is BACK, hand them the state of exactly the businesses
        /// that were simulated in their absence. r2 F4: the send does NOT clear the mark - it flags it
        /// ReturnSent and the OWNER'S ACK clears it - so a return lost to a drop mid-delivery re-fires at
        /// the owner's next load instead of vanishing. Called at the peer's applying edge (their own load
        /// is provably done) and again from the reconcile while that edge has already passed; a mark that
        /// is already ReturnSent is skipped here AND there, so nothing ever double-sends. The marks are
        /// collected FIRST so the loop never walks the table while a send mutates it.</summary>
        internal static void HostReturnIfMarked(string pid, bool broadcast)
        {
            try
            {
                if (!_running || string.IsNullOrEmpty(pid) || MergerAbsence.MarkCount == 0) return;
                string stable = ""; try { stable = StableOfPid(pid) ?? ""; } catch { }
                var due = new List<MergerAbsence.AbsenceMark>();
                foreach (var kv in MergerAbsence.Marks)
                {
                    var m = kv.Value;
                    if (m == null || !m.OwnerBack || m.ReturnSent) continue;   // r2 F4: one send per mark
                    if (m.OwnerPid != pid && (stable.Length == 0 || m.OwnerStable != stable)) continue;
                    due.Add(m);
                }
                int sent = 0;
                foreach (var m in due)
                {
                    // The toast's name: the character name of the LAST machine that simulated these
                    // shops (DisplayNameFor already falls back to the player id). Empty only when no
                    // simulator was ever recorded - a mark restored from a pre-P3-C manifest.
                    string ranBy = string.IsNullOrEmpty(m.LastSimulatorPid) ? "" : DisplayNameFor(m.LastSimulatorPid);
                    if (MergerAbsence.HostSendReturn(m, ranBy, pid)) sent++;
                }
                if (sent > 0 && broadcast) RefreshGrantsAndBroadcast();   // r2 F4: the marks stay until the owner acks
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return trigger for '{pid}': {ex.Message}"); }
        }

        /// <summary>HOST, MAIN THREAD (MERGER PHASE 3-C r2, F4): the RETURNED OWNER acked their return.
        /// The SENDER is the only identity that counts here - the payload names nobody the host trusts -
        /// and only a mark whose owner that sender IS (by player id or by stable id) is cleared. Clearing
        /// is what ends StorePaperwork's OwnerBack guard and takes the owner out of the broadcast absence
        /// table, so the state goes out straight after.</summary>
        internal static void HostReturnAck(string senderPid)
        {
            try
            {
                if (!_running || string.IsNullOrEmpty(senderPid)) return;
                string stable = ""; try { stable = StableOfPid(senderPid) ?? ""; } catch { }
                if (MergerAbsence.HostClearMarkOnReturnAck(senderPid, stable) > 0) RefreshGrantsAndBroadcast();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return ack from '{senderPid}': {ex.Message}"); }
        }

        /// <summary>HOST, MAIN THREAD (P3-B r4 F3): re-send ONE hand-over to the machine that already
        /// holds the mark. Only the CURRENT simulator of that owner can ask, so a stale request from a
        /// re-designated machine (or a forged one) serves nothing. It re-enters SendHandover, which is
        /// the same one-shot the first designation used - and ApplyHandover undoes before it installs,
        /// so arriving twice is harmless (D15).</summary>
        private static void HostResendHandover(string simPid, string ownerStable)
        {
            try
            {
                if (!_running || string.IsNullOrEmpty(simPid) || string.IsNullOrEmpty(ownerStable)) return;
                if (!MergerAbsence.Marks.TryGetValue(ownerStable, out var m) || m == null || m.SimulatorPid != simPid)
                {
                    Plugin.Logger.LogInfo($"[Absence] re-send asked by '{simPid}' for '{ownerStable}' - not its mark, ignored.");
                    return;
                }
                // r6 F3 (m-b): ONE service per owner per 30 s. The asking side retries on a 10 s cadence
                // and every service queues one interior snapshot per address while Tick drains ONE per
                // second, so an unthrottled loop outran the drain for any owner with more than 10 shops.
                if (!HostResendDue(ownerStable, out bool logRefusal))
                {
                    if (logRefusal)   // r7: once per throttle window, not once per ask
                        Plugin.Logger.LogInfo($"[Absence] re-send asked by '{simPid}' for '{ownerStable}' - throttled (one service per 30 s).");
                    return;
                }
                Plugin.Logger.LogInfo($"[Absence] re-sending the hand-over of '{m.OwnerPid}' to '{simPid}' (its installs were lost).");
                MergerAbsence.SendHandover(m);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] re-send: {ex.Message}"); }
        }

        /// <summary>HOST (r6 F3): true at most once per owner per 30 s. The TickCount delta is unchecked, so
        /// the wrap is harmless. `logRefusal` is true for the FIRST refusal of a window only (r7). Both
        /// tables are cleared by ResetPaperwork (new game; manifest restore on load) - both precede any
        /// mark of the next session, so a stale stamp cannot delay its first re-send.</summary>
        private static readonly Dictionary<string, int> _resendServedAt = new();
        private static readonly HashSet<string> _resendThrottleLogged = new();
        private static bool HostResendDue(string ownerStable, out bool logRefusal)
        {
            string k = ownerStable ?? "";
            int now = Environment.TickCount;
            lock (_resendServedAt)
            {
                if (_resendServedAt.TryGetValue(k, out var at) && unchecked(now - at) < 30000)
                {
                    logRefusal = _resendThrottleLogged.Add(k);
                    return false;
                }
                _resendServedAt[k] = now;
                _resendThrottleLogged.Remove(k);
                logRefusal = false;
                return true;
            }
        }

        /// <summary>HOST: of these ONLINE pids, the one connected LONGEST. JoinedAtByPid is stamped at
        /// Hello with Environment.TickCount, so the elapsed figure (unchecked, wrap-safe) is the age of
        /// the connection. No stamp at all -> the list's first entry, which is join order.</summary>
        private static string LongestOnlineOf(List<string> online)
        {
            string best = online.Count > 0 ? online[0] : "";
            int bestAge = -1;
            foreach (var pid in online)
            {
                if (!JoinedAtByPid.TryGetValue(pid, out var at)) continue;
                int age = unchecked(Environment.TickCount - at);
                if (age > bestAge) { bestAge = age; best = pid; }
            }
            return best;
        }

        /// <summary>HOST: every building the rental ledger attributes to this StableId.</summary>
        private static List<string> AddressesOfStable(string stable)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(stable)) return list;
            foreach (var kv in BuildingOwners)
            {
                string s = kv.Value == "host" ? MPConfig.StableId
                         : StableIdByPlayer.TryGetValue(kv.Value, out var os) ? (os ?? "") : "";
                if (!string.IsNullOrEmpty(s) && s == stable) list.Add(kv.Key);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public static List<MpPaperworkEntry> SnapshotPaperwork()
        {
            var list = new List<MpPaperworkEntry>();
            lock (_paperwork)
                foreach (var kv in _paperwork)
                    list.Add(new MpPaperworkEntry { StableId = kv.Value.StableId, Day = kv.Value.Day, Json = kv.Value.Json });
            return list;
        }

        /// <summary>Host: REPLACE the store from the manifest being restored — clear-then-apply like
        /// the grant and merger restores it sits beside. The loaded slot's paperwork is the only
        /// paperwork there is: nothing survives in memory across a load, so an older save can never
        /// pull newer paperwork (user rule 2026-09-11). A manifest written before the field existed
        /// restores an empty store.</summary>
        public static void RestorePaperworkFromManifest(MpManifest m)
        {
            ResetPaperwork();
            try
            {
                int n = 0;
                if (m?.Paperwork != null)
                    lock (_paperwork)
                        foreach (var e in m.Paperwork)
                        {
                            if (string.IsNullOrEmpty(e?.StableId)) continue;
                            _paperwork[e.StableId] = new PaperworkEntry
                            {
                                StableId    = e.StableId,
                                Day         = e.Day,
                                ReceivedDay = 0,
                                Json        = e.Json ?? "",
                            };
                            n++;
                        }
                Plugin.Logger.LogInfo($"[Paperwork] restored {n} member bundle(s) from the manifest.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] manifest restore: {ex.Message}"); }
        }

        /// <summary>Merger phase 4a (G1): the host's COMPANY BOOKS store for the manifest MODEL, beside
        /// the paperwork store and the absence marks it rides with. Keyed by STABLE id like both of them
        /// (a player id is live and dies with the session); the bundle goes in as TEXT, a straight
        /// passthrough of what the sender serialised. A member with no known stable id is skipped - it
        /// republishes at its next day change or membership edge.</summary>
        public static List<MpCompanyBooksEntry> SnapshotCompanyBooks()
        {
            var list = new List<MpCompanyBooksEntry>();
            try
            {
                int hostDay = SaveGameManager.Current?.Day ?? 0;
                foreach (var kv in CompanyBooks.HostStoreSnapshot())
                {
                    var p = kv.Value;
                    if (p == null) continue;
                    string stable = !string.IsNullOrEmpty(p.StableId) ? p.StableId
                                  : (StableIdByPlayer.TryGetValue(kv.Key, out var s) ? (s ?? "") : "");
                    if (string.IsNullOrEmpty(stable)) { Plugin.Logger.LogInfo($"[Books] manifest snapshot skipped '{kv.Key}': no stable id known for that member yet."); continue; }
                    list.Add(new MpCompanyBooksEntry
                    {
                        StableId    = stable,
                        Day         = p.Day,
                        ReceivedDay = hostDay,
                        Json        = Newtonsoft.Json.JsonConvert.SerializeObject(p),
                    });
                }
                list.Sort((x, y) => string.CompareOrdinal(x.StableId, y.StableId));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] manifest snapshot: {ex.Message}"); }
            return list;
        }

        /// <summary>Host: REPLACE the books store from the manifest being restored - clear-then-apply
        /// beside the paperwork restore, on the same timeline rule: the loaded slot's books are the only
        /// books there is, and an older slot can never pull the newer world's (user rule 2026-09-11). The
        /// owner's PID is re-keyed to whoever holds that stable id in THIS session; a member who is not
        /// here keeps the pid the bundle was stored with and is simply re-keyed at its next publish. A
        /// manifest written before the field existed restores an empty store.</summary>
        public static void RestoreCompanyBooksFromManifest(MpManifest m)
        {
            try
            {
                CompanyBooks.HostReset();
                int n = 0;
                if (m?.CompanyBooks != null)
                    foreach (var e in m.CompanyBooks)
                    {
                        if (string.IsNullOrEmpty(e?.StableId) || string.IsNullOrEmpty(e.Json)) continue;
                        CompanyBooksPayload? p = null;
                        try { p = Newtonsoft.Json.JsonConvert.DeserializeObject<CompanyBooksPayload>(e.Json); }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] manifest entry for '{e.StableId}' unreadable: {ex.Message} - that member republishes at its next day change."); }
                        if (p == null) continue;
                        p.StableId = e.StableId;
                        if (e.Day > 0) p.Day = e.Day;
                        // m-c (review r3): the serialised bundle still carries LAST session's player
                        // id.  Blank it first, or the entry lands under a dead id - inert, yet it
                        // rides every later manifest and two entries could share one stable id.
                        p.OwnerPid = "";
                        foreach (var kv in StableIdByPlayer)
                            if (kv.Value == e.StableId) { p.OwnerPid = kv.Key; break; }
                        if (string.IsNullOrEmpty(p.OwnerPid))
                            Plugin.Logger.LogInfo($"[Books] manifest entry for '{e.StableId}' kept under its stable id: that member is not online yet - it is adopted when it connects.");
                        CompanyBooks.HostRestore(p);
                        n++;
                    }
                Plugin.Logger.LogInfo($"[Books] restored {n} member bundle(s) from the manifest.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] manifest restore: {ex.Message}"); }
        }

        /// <summary>Merger phase 3-B: the host's absence marks for the manifest MODEL, beside the
        /// paperwork store they belong with.</summary>
        public static List<MpAbsenceMark> SnapshotAbsence()
        {
            var list = new List<MpAbsenceMark>();
            try
            {
                // P3-C: the LIVE table, not HostSnapshot() - the broadcast shape (AbsenceInfo) has no
                // room for LastSimulatorPid, and once r5 clears SimulatorPid at the owner's return that
                // field is the only record of who actually ran the shops (the return toast's name).
                foreach (var kv in MergerAbsence.Marks)
                {
                    var a = kv.Value;
                    if (a == null) continue;
                    list.Add(new MpAbsenceMark
                    {
                        OwnerStable      = a.OwnerStable,
                        OwnerPid         = a.OwnerPid,
                        SimulatorPid     = a.SimulatorPid,
                        LastSimulatorPid = a.LastSimulatorPid,
                        Addresses        = new List<string>(a.Addresses ?? new List<string>()),
                        SinceDay         = a.SinceDay,
                    });
                }
                list.Sort((x, y) => string.CompareOrdinal(x.OwnerStable, y.OwnerStable));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] manifest snapshot: {ex.Message}"); }
            return list;
        }

        /// <summary>Host: REPLACE the absence table from the manifest being restored - clear-then-apply
        /// beside the paperwork restore, and BEFORE the first HostReconcileAbsence can run. A restored
        /// mark keeps its SinceDay (nothing else records when the absence began) and comes back with NO
        /// simulator: a player id from the previous session names nobody here, so the reconcile
        /// re-designates and re-sends the hand-over. A manifest written before the field existed
        /// restores an empty table.</summary>
        public static void RestoreAbsenceFromManifest(MpManifest m)
        {
            try
            {
                int n = 0;
                if (m?.Absence != null)
                    foreach (var a in m.Absence)
                    {
                        if (string.IsNullOrEmpty(a?.OwnerStable)) continue;
                        MergerAbsence.HostRestoreMark(a.OwnerStable, a.OwnerPid, a.Addresses, a.SinceDay, a.LastSimulatorPid);
                        n++;
                    }
                if (n > 0)
                    Plugin.Logger.LogInfo($"[Absence] restored {n} absence mark(s) from the manifest (simulator to be re-designated).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] manifest restore: {ex.Message}"); }
        }

        /// <summary>Host: send each connected client its own stored .hsg for the
        /// session so it can load in.  Phase 4 load.</summary>
        /// <summary>Stable ids that are connected RIGHT NOW (the host itself + every current peer).
        /// These save themselves through the SaveNow round-trip, so the save carry-forward must NOT touch
        /// them (avoids racing their fresh upload); it only carries members who are absent.</summary>
        public static System.Collections.Generic.HashSet<string> ConnectedStableIds()
        {
            var set = new System.Collections.Generic.HashSet<string>();
            try
            {
                set.Add(MPConfig.StableId);   // the host
                foreach (var pid in _peerNames.Values)
                    if (StableIdByPlayer.TryGetValue(pid, out var s) && !string.IsNullOrEmpty(s)) set.Add(s);
            }
            catch { }
            return set;
        }

        /// <summary>Round-269: route a granted guest's conveyed grab to the shop's OWNER —
        /// their apply + next owner-push propagates everywhere. Owner offline (ledger holds
        /// a stable id, no peer) or the host owns it → apply to the host's world copy, the
        /// source every future sync serves. Also called host-locally for the host's own
        /// grabs in a client's shop.</summary>
        internal static void HandleGuestCargoGrab(string senderPid, GuestCargoGrabPayload? p)
        {
            if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.ItemInstanceId)) return;
            try
            {
                string ownerLedger = BuildingOwners.TryGetValue(p.AddressKey, out var o) ? (o ?? "") : "";
                if (!GameStatePatcher.IsHostLedgerId(ownerLedger) && !string.IsNullOrEmpty(ownerLedger) && ownerLedger != senderPid)
                {
                    var peer = PeerForPid(ownerLedger);
                    if (peer != null)
                    {
                        Send(peer, MessageEnvelope.Create(MessageType.GuestCargoGrab, senderPid, p));
                        return;
                    }
                }
                GameStatePatcher.ApplyGuestCargoGrab(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] GuestCargoGrab: {ex.Message}"); }
        }

        /// <summary>Display PlayerIds of every currently connected client (bug-report v2:
        /// how many peer-log replies a host-side report should wait for).</summary>
        internal static List<string> ConnectedPids()
        {
            try { return new List<string>(_peerNames.Values); }
            catch { return new List<string>(); }
        }

        // Round-37: `session` = the folder the .hsg bytes are READ from (may be a frozen checkpoint);
        // `lineageName` = the session name the CLIENTS adopt as active (the playthrough base), so their
        // later uploads/disconnect saves continue the lineage and never mutate the loaded checkpoint.
        public static void SendLoadDataToEachClient(string session, MpManifest m, string? lineageName = null)
        {
            string adopt = string.IsNullOrEmpty(lineageName) ? session : lineageName;
            foreach (var peer in _clients.Keys)
            {
                if (!_peerNames.TryGetValue(peer.Id, out var pid)) continue;
                if (!StableIdByPlayer.TryGetValue(pid, out var stable) || string.IsNullOrEmpty(stable)) continue;
                // Round-184: BOTH serve paths resolve through the ONE ladder (exact session →
                // lineage rescue → unavailable → fresh) — the duplicated copies drifted and a
                // fix landed in only one (rig-caught: the lobby-start rejoin still fresh-started
                // after only the mid-join path got the rescue).
                var verdict = MPSaveCoordinator.ResolveMemberSave(session, stable, out var data, out var servedFrom, out var cash);
                if (data == null)
                {
                    // Proposal 2 (2026-06-17): distinguish a brand-NEW player (no saved character — fresh-start
                    // is correct) from a RETURNING one whose .hsg we can't read right now. A manifest slot exists
                    // once a player has ever been saved this session, so slot-present + null .hsg means their
                    // real character exists but the file is missing/locked/corrupt. Fresh-starting them would
                    // abandon that save (and, once they re-save, overwrite any chance of recovery) — so refuse,
                    // and tell the client so the player can reconnect to retry / the host can recover the file.
                    if (verdict == MPSaveCoordinator.ServeVerdict.Unavailable)
                    {
                        Send(peer, MessageEnvelope.Create(MessageType.LoadData, "host", new LoadDataPayload
                        {
                            SessionName      = adopt,
                            HsgGzipBase64    = "",
                            SaveUnavailable  = true,
                            FallbackSettings = LastStartSettings,
                        }));
                        Plugin.Logger.LogError($"[Server] Returning player '{pid}' (stable={stable}) has a save slot but its .hsg is unreadable — REFUSING to fresh-start (would abandon their character). Sent save-unavailable.");
                        continue;
                    }
                    // Genuinely new player (no slot to protect) — empty payload = fresh character with host
                    // settings. (The old silent `continue` left them stuck in the lobby — user bug 2026-06-12.)
                    float kc = GetKnownCash(pid);
                    Send(peer, MessageEnvelope.Create(MessageType.LoadData, "host", new LoadDataPayload
                    {
                        SessionName      = adopt,
                        HsgGzipBase64    = "",
                        Money            = Math.Max(0f, kc),
                        FallbackSettings = LastStartSettings,
                        // Round-217: a fresh character still joins a NAMED world — identity
                        // at birth means this is never blank (the rig case: blank here left
                        // the client with no active playthrough, and its first save landed
                        // in '_unresolved').
                        PlaythroughId    = MPSaveCoordinator.ActivePlaythroughId,
                        LoadGen          = MintLoadGen(pid),   // round-284: a fresh start is a served load too
                    }));
                    Plugin.Logger.LogWarning($"[Server] No save slot for new player '{pid}' (stable={stable}) — sent fresh-character fallback.");
                    continue;
                }
                // Cash came from the served session's manifest (rescue-aware); world identity
                // below stays the LOADED session's (m) — the world everyone is jumping to.
                // Round-224: zero cash with no live stream = UNKNOWN, not broke — the
                // overlay must stand down so the .hsg's own wallet survives (rig: a
                // placeholder $0 slot zeroed a returning client's wallet).
                bool moneyKnown = cash != 0f || GetKnownCash(pid) >= 0f;
                if (!moneyKnown) Plugin.Logger.LogInfo($"[Server] cash unknown for '{pid}' — serving no-overlay; their save's own wallet stands.");
                var payload = new LoadDataPayload
                {
                    SessionName   = adopt,
                    RawLength     = data.Value.raw,
                    MetaJson      = MPSaveCoordinator.ReadMemberMetaJson(servedFrom, stable),   // round-275b: keep their copy datable
                    Money         = cash,
                    MoneyKnown    = moneyKnown,
                    // Handoff slice 4: world identity/day/epoch for the joiner's
                    // rollback-consent check.
                    WorldDay      = m.WorldDay,
                    // Round-217: never send a blank identity — a pre-first-save manifest
                    // may not be stamped yet, but the live world always knows itself.
                    PlaythroughId = !string.IsNullOrEmpty(m.PlaythroughId) ? m.PlaythroughId : MPSaveCoordinator.ActivePlaythroughId,
                    HostEpoch     = m.HostEpoch,
                    LoadGen       = MintLoadGen(pid),   // round-284 load ticket
                };
                var ldEnv = MessageEnvelope.Create(MessageType.LoadData, "host", payload);
                ldEnv.Attachment = data.Value.gz;   // v9: raw gzip rides the attachment frame
                Send(peer, ldEnv);
                Plugin.Logger.LogInfo($"[Server] Sent LoadData to '{pid}' ({data.Value.raw}B, ${cash:F0}).");
            }
        }

        /// <summary>
        /// PlayerId → CharacterName (Wave 5).  Populated by PlayerProfile
        /// messages from each connected client + host's own profile.
        /// Used as the display name in RivalsSnapshot / RivalsStatsSnapshot.
        /// </summary>
        // CONCURRENT: written on the poll thread (PlayerProfile handler) and the
        // main thread (host's own profile); read on both during snapshot builds.
        public static readonly ConcurrentDictionary<string, string> _characterNamesByPlayerId = new();

        /// <summary>
        /// PlayerId → most recent self-reported stats from that client (Wave 7).
        /// Captured when client sends RivalsStatsRequest; used by host's
        /// BuildRivalsStatsSnapshot to populate non-host player rows since
        /// host has no source of truth for what other players own locally.
        /// </summary>
        // CONCURRENT: written on the poll thread (RivalsStatsRequest handler),
        // enumerated on the main thread (rival-fairness patches, snapshot build).
        private static readonly ConcurrentDictionary<string, RivalsStatsRequestPayload> _clientSelfStats = new();

        /// <summary>Self-reported weekly income for a session player's business
        /// at this address (0 if unknown).  Bridges the rival-AI fairness
        /// patches: the host's replica registrations have empty order history,
        /// so "is this business succeeding" reads the leaderboard stats.</summary>
        public static float SessionBusinessWeeklyIncome(string addressKey)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey)) return 0f;
                foreach (var kv in _clientSelfStats)
                {
                    var list = kv.Value?.Businesses;
                    if (list == null) continue;
                    foreach (var b in list)
                        if (b != null && b.AddressKey == addressKey) return b.WeeklyIncome;
                }
            }
            catch { }
            return 0f;
        }
        private static volatile bool _running;

        // ── Startup pause hold ────────────────────────────────────────────────
        // Players (by ID) confirmed to have finished loading their game scene.
        // The game stays frozen at timeScale 0 until every roster player is in
        // this set, then the host releases it for everyone.  One-shot per game.
        private static readonly HashSet<string> _inGamePlayers = new();
        // Frozen-until-synced: a player is "world ready" once it has APPLIED the world
        // sync (host = as soon as its own world is loaded; clients = after they ack
        // WorldReady).  The startup hold releases only when ALL players are world-ready,
        // so nobody gets control of an un-synced world.
        private static readonly HashSet<string> _worldReadyPlayers = new();
        private static bool _hostSnapshotsReady;   // host's own world loaded → can serve snapshots
        private static readonly object _startupLock = new();
        /// <summary>Monotonic milliseconds (net48 has no Environment.TickCount64).</summary>
        private static long TickMs64 => System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000L);
        private static bool _startupReleased;
        // True while the session is paused because a player DROPPED (set in
        // OnPeerDisconnected, cleared when they reconnect) — distinguishes the
        // disconnect pause from a deliberate manual pause so the reconnect path
        // only lifts the former.
        private static volatile bool _pausedByDisconnect;
        // Set ONLY by the deliberate pause button (host TogglePause patch + a client's
        // ManualPause), NEVER by the disconnect pause.  ResumeFromDisconnectPause restores
        // the shared pause to THIS, so lifting a disconnect pause can't cancel a pause a
        // player set on purpose (M6, 2026-06-24).
        private static volatile bool _deliberatePause;   // 284b (verifier F-5): read lock-free on the poll thread (join replay :2365, heartbeat stamp) — volatile like its sibling _pausedByDisconnect
        /// <summary>Who dropped (for the host's pause overlay).</summary>
        public static volatile string DisconnectPauseWho = "";
        public static bool PausedByDisconnect => _pausedByDisconnect;

        /// <summary>Lift a disconnect pause — from the overlay's "keep playing"
        /// click or automatically when the dropped player reconnects.</summary>
        public static void ResumeFromDisconnectPause()
        {
            if (!_pausedByDisconnect) return;
            _pausedByDisconnect = false;
            DisconnectPauseWho  = "";
            BroadcastManualPause(_deliberatePause);   // restore to the deliberate-pause state, not blindly unpaused — a deliberate pause survives the reconnect (M6)
            GameStatePatcher.EnqueueOnMainThread(() => TimeSync.SetManualPause(_deliberatePause));
            Plugin.Logger.LogInfo($"[Server] Disconnect pause lifted (deliberate pause still {(_deliberatePause ? "ON" : "off")}).");
        }

        public static bool IsRunning      => _running;
        public static int  ConnectedCount => _clients.Count;

        public static bool Start(int port)
        {
            MPClient.LastDisconnectReason = ""; MPClient.FriendlyDisconnectReason = null;   // CONNECT-MSG r1 review (F-2026-09-06-AG MINOR-5): a stale join reason must never show on a failed host bind
            // Round-229: with a SYSTEMIC boot patch failure (>= ModEntry.PatchFailHardBlock
            // classes threw) the game hooks are unreliable — hosting would half-work
            // (lobby up, host's own load bounces, clients sent into a world the host
            // never reaches). Refuse up front, loudly. A handful of failures only
            // warns at the menu; MP stays enabled (light touch, user-directed).
            if (ModEntry.MpDisabledByPatchFailure)
            {
                Plugin.Logger.LogError($"[Server] Hosting refused: {ModEntry.PatchFailureNotice}");
                try { UI.Notification.Notifications.Show(UI.Notification.NotificationType.Error, ModEntry.PatchFailureNotice); } catch { }
                return false;
            }
            // A previous session may still be live (e.g. the host exited to the
            // menu without Leave — nothing stops the server on scene exit prior
            // to this guard).  Tear it down first: the old transport still holds
            // the port, so the new bind fails with AddressAlreadyInUse while
            // _running still reads true — a zombie host whose lobby looks alive
            // but that nobody can connect to.
            if (_running)
            {
                Plugin.Logger.LogInfo("[Server] Start: previous session still running — stopping it first.");
                Stop();
            }

            // Reset lobby state for a fresh session
            IsInLobby = true;
            LobbyReset(MPConfig.PlayerId); // host is always the first player
            PlayerColours.ResetHost();   // colours r2 (MINOR-3): a new hosted world starts with an empty slot table; a LOADED world is re-seeded from its manifest by RestoreOwnershipFromManifest
            PlayerColours.ResetSession();   // 2026-09-05 colours: the session slot map dies with the session, like the roster below
            GameStatePatcher.ClientPlayerRoster.Clear();   // review r9 #6: a fresh session starts with an empty roster (it refills from PlayerProfiles and rides the rivals snapshot). Here, not in Stop(): a machine that was a GUEST in the previous world never ran Stop(), and its roster would ship that world's members to this world's clients.
            MPClient.OfflineFork = false;   // H-FORK-1 r2 (review #4): a fresh hosted session is never an offline fork
            EnforceStartingCash = true;
            StartingCashByPlayer.Clear();
            StartingAgeByPlayer.Clear();
            ChosenLoadSession = "";   // cleared each host session; set only when a save is picked
            MPSaveCoordinator.ActiveSessionName = "";   // stale session must not leak into a new lobby
                                                        // (HostLoadSession / first save re-set it)
            _peerNames.Clear();
            _peerBuild.Clear();       // round-281: per-peer build records die with the session, like _peerNames
            StableIdByPlayer.Clear();
            StableIdByPlayer[MPConfig.PlayerId] = MPConfig.StableId; // host's own
            PlayerColours.Learn(MPConfig.PlayerId, PlayerColours.HostAssign(MPConfig.StableId));   // 2026-09-05 colours: the host holds a permanent slot too
            MPLog.BeginSession(System.Guid.NewGuid().ToString("N").Substring(0, 8), "host");
            _clients.Clear();         // stale peers from a torn-down session
            BuildingOwners.Clear();   // per-session state — a new game must not inherit
            _sharedPoolByOwner.Clear();   // shared-shop slice 3: cached benches are per session too
            BuildingRealEstateOwners.Clear(); // bought-real-estate ledger — same per-session lifecycle (the load path re-seeds it from the manifest); was leaking across a new game and locking fresh-world buildings un-buyable
            CashByStableId.Clear();   // owners/cash; the load path re-seeds from the manifest
            _characterNamesByPlayerId.Clear();   // per-session: a returning player must not collide with a stale name
            _clientSelfStats.Clear();            // …or stale self-reported stats (feeds rival-fairness targeting)
            var t = new LnlHostTransport();
            t.PeerConnected    += OnPeerConnected;
            t.PeerDisconnected += OnPeerDisconnected;
            t.Received         += OnReceive;
            _transport = t;

            if (!t.Start(port))
            {
                Plugin.Logger.LogError($"[Server] Failed to start on port {port}");
                _transport = null;
                return false;
            }

            // Steam relay listener BESIDE UDP (slice 2): same three handlers — the
            // seam makes relay peers indistinguishable above the transport.  Steam
            // being unavailable must never block hosting (UDP-only fallback logs).
            try
            {
                var st = new SteamHostTransport();
                st.PeerConnected    += OnPeerConnected;
                st.PeerDisconnected += OnPeerDisconnected;
                st.Received         += OnReceive;
                _steamTransport = st.Start(0) ? st : null;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Steam relay listener: {ex.Message} — UDP only."); _steamTransport = null; }

            _running = true;
            ResetJoinControl();   // fresh hosting session — bans lift, pending requests drop
            MPSteamPresence.AdvertiseHosting();   // friends see Join Game / can be invited (no-op without Steam)

            Plugin.Logger.LogInfo($"[Server] Listening on port {port}");
            MPNet.FetchPublicIpAsync();                          // public IP for the lobby "Show IP"
            MPNet.TryForwardAsync(port, MPConfig.LocalLanIp());  // best-effort UPnP open of UDP <port> (fails safe)
            return true;
        }

        public static void Stop()
        {
            LastStartSettings = null;   // H-FRESH-1 r2: a session's start settings die with it — the next world describes its own (review F-2026-09-06-E MAJOR-1)
            // Initiator forensics: clients see our Stop as RemoteConnectionClose
            // with no clue who pulled the plug — name the caller here so the
            // load-start kick (2026-06-12, cause unresolved) is attributable.
            try { PlayerColours.ResetSession(); PlayerColours.ResetHost(); } catch { }   // colours r3 (review r2 MINOR-2): the host's maps die with the session, like the client's on disconnect
            if (_running)
                Plugin.Logger.LogWarning($"[Server] STOP called ({_clients.Count} client(s) will see RemoteConnectionClose) from: {Environment.StackTrace}");
            // Round-184: a save made moments ago may still be waiting on member uploads — those
            // will never arrive once the transport stops; complete its carry-forward first.
            try { MPSaveCoordinator.FlushCarryBackstopsNow("server stop"); } catch { }
            _running = false;
            // Audit 2026-08-26: pending merger proposals live ONLY here and are cleared only by an
            // answer or a withdrawal — they were on none of the three teardown paths. Meanwhile each
            // player's own copy IS cleared on every return to the main menu, and both the accept/decline
            // and the withdraw buttons are gated on that copy. So one unanswered proposal + a trip to
            // the menu left the host holding a record neither player could resolve, and both guards
            // return SILENTLY: that pair could never merge again until the process restarted.
            _mergerPendingByTarget.Clear();
            MPNet.RemoveMappingAsync();   // best-effort UPnP cleanup (harmless if it can't run)
            _transport?.Stop();
            _transport = null;
            try { _steamTransport?.Stop(); } catch { }
            _steamTransport = null;
            MPSteamPresence.ClearAdvertise();
            IsInLobby = true;
            LobbyClear();
            // Round-253e (user test: the mismatch notice outlived the client AND a re-host):
            // a stopped lobby is a fresh start — drop the per-player mismatch records (a
            // rejoining client re-diffs at Hello anyway) and any notice still on the strip.
            ModMismatchByPlayer.Clear();
            try { MPCanvasUI.ClearLobbyNotice(); } catch { }
            _peerNames.Clear();
            _peerBuild.Clear();       // round-281
            _clients.Clear();
            MPSaveCoordinator.ConsumeDevHostLoadAs("session stop");   // round-285: the impersonation override dies with the session
            lock (_startupLock) { _inGamePlayers.Clear(); _worldReadyPlayers.Clear(); _fenceExcused.Clear(); _peerPhase.Clear(); _peerPhaseSeq.Clear(); _gateHeal.Clear(); _fenceArmedAtMs = TickMs64; _hostSnapshotsReady = false; _startupReleased = false; _pausedByDisconnect = false; _deliberatePause = false; }
            ClearV9SessionMaps();   // v9 review MIN-5: mirror memory, join baselines, applying latches die with every (re)arm
            lock (_joinBaselineDone) _joinBaselineDone.Clear();   // round-276: baseline latches die with the session
            _expectedLoadGen.Clear(); _parkedBaselineGen.Clear(); _firedLoadGen.Clear(); _markedInGame.Clear();   // round-284: load-ticket state too (the counter itself never resets — a reissued gen could match a stale echo)
            try { PlayerColours.ResetSession(); } catch { }   // colours r4 (review r3 MINOR-2): a Hello handled during teardown cannot leave an orphan picker row
        }

        /// <summary>Host clicked "Start New Game" in the lobby.</summary>
        /// <summary>Settings of the last new-game start — the mid-join fresh-
        /// character fallback reuses them (null when the host loaded a save).</summary>
        public static GameVariablesDto? LastStartSettings;

        /// <summary>PROTON-1: the save-version refusal in the two start paths below is logged
        /// at most once per process -- the Start button can be clicked repeatedly.</summary>
        private static bool _versionRefusalLogged;

        public static void StartNewGame(GameVariablesDto settings)
        {
            if (!_running) return;
            // PROTON-1: refuse to start a session this machine cannot write saves for -- an
            // unresolved version folder turns every MP store path relative, silently.
            MPSaveManager.EnsureVersionCached();
            if (string.IsNullOrEmpty(MPSaveManager.CachedVersionFolderOrEmpty))
            {
                if (!_versionRefusalLogged)
                {
                    _versionRefusalLogged = true;
                    Plugin.Logger.LogError("[MPSave] REFUSING to start: the game's save version folder cannot be resolved on this machine (PROTON-1)");
                }
                return;   // lobby untouched -- nothing has flipped yet
            }
            LastStartSettings = settings;
            MPLoadProfiler.Mark($"HOST StartNewGame ({settings.Difficulty}) — {_clients.Count} client(s)");
            // Round-217: identity at birth — mint the playthrough BEFORE anything can be
            // sent to a joiner, so no welcome package ever says "world: (blank)".
            MPSaveCoordinator.HostBeginNewWorldIdentity();
            IsInLobby = false;
            GrantSync.ResetStore();   // fresh world — no grants; flush any store left by a previously loaded
                                      // session (the scene reset no longer wipes the store, 2026-06-30)
            MergerSync.ResetStore();  // fresh world — no merger (same session-boundary lifecycle)
            ResetPaperwork();         // phase 3-A (review r1 MAJOR-2): the paperwork store dies with the session too — a new world never inherits the old world's bundles
            try { PaperworkSync.Reset(); } catch { }   // and this machine's publisher forgets the previous world's day/edge
            MergerAbsence.HostReset(); MergerAbsence.Reset();   // phase 3-B: the absence marks die with the session too - a new world starts with nobody simulating for anybody
            _mergerPendingByTarget.Clear();   // and no proposals carried in from the previous world (audit 2026-08-26)
            ResetWallet();            // fresh world — no shared wallet (slice 4)
            // 4c part 2b r4c (re-check r4 MAJOR-1): the host-held in-transit tables die with the session too - a new world
            // must never resume the previous world's employee moves or cargo (they were cleared only by a manifest LOAD).
            // The previous world's transit tail stays on disk untouched: it continues THAT world's last save (its stamp).
            lock (_transfers) _transfers.Clear();
            lock (_cargo) { _cargo.Clear(); _cargoSeq = 0; }
            try { CargoTransfer.ResetSession(); } catch { }
            MPSaveCoordinator.ConsumeDevHostLoadAs("new game");   // round-285: a fresh world has no member slots to impersonate

            // Re-arm the startup pause hold for this new game.
            lock (_startupLock) { _inGamePlayers.Clear(); _worldReadyPlayers.Clear(); _fenceExcused.Clear(); _peerPhase.Clear(); _peerPhaseSeq.Clear(); _gateHeal.Clear(); _fenceArmedAtMs = TickMs64; _hostSnapshotsReady = false; _startupReleased = false; _pausedByDisconnect = false; _deliberatePause = false; }
            ClearV9SessionMaps();   // v9 review MIN-5: mirror memory, join baselines, applying latches die with every (re)arm
            lock (_joinBaselineDone) _joinBaselineDone.Clear();   // round-276: baseline latches die with the session
            _expectedLoadGen.Clear(); _parkedBaselineGen.Clear(); _firedLoadGen.Clear(); _markedInGame.Clear();   // round-284: load-ticket state too (the counter itself never resets — a reissued gen could match a stale echo)

            // Per-player starting cash: each client gets the host-designated amount
            // (their override, else the difficulty base).  The host now designates
            // everyone's cash, so EnforceStartingCash is always true here — the
            // client uses the StartingMoney we bake into its own Settings copy.
            int baseCash = settings.StartingMoney;
            int baseAge  = settings.StartingAge;
            foreach (var peer in _clients.Keys)
            {
                string pid  = _peerNames.TryGetValue(peer.Id, out var p) ? p : "";
                int    cash = StartingCashFor(pid, baseCash);
                int    age  = StartingAgeFor(pid, baseAge);
                var perPayload = new StartGamePayload
                {
                    SaveName            = "",
                    Settings            = CloneWithCash(settings, cash, age),
                    EnforceStartingCash = true,
                    LoadGen             = string.IsNullOrEmpty(pid) ? 0 : MintLoadGen(pid),   // round-284 load ticket
                };
                Send(peer, MessageEnvelope.Create(MessageType.StartGameNew, "host", perPayload));
                Plugin.Logger.LogInfo($"[Server] StartNewGame → '{pid}' cash ${cash} age {age}.");
            }
            Plugin.Logger.LogInfo($"[Server] StartNewGame ({settings.Difficulty}) sent to {_clients.Count} client(s); base cash ${baseCash}.");

            // Host's own starting cash + age (the host's overrides if set, else base).
            int hostCash = StartingCashFor(MPConfig.PlayerId, baseCash);
            int hostAge  = StartingAgeFor(MPConfig.PlayerId, baseAge);
            var hostSettings = CloneWithCash(settings, hostCash, hostAge);

            // Route the host through the game's own intro/character-creation scene.
            // LoadGame() called without character data leaves the loading screen stuck;
            // LoadIntro() is the correct entry point — it sets up character data and
            // then naturally transitions to the game when the player clicks "Start Game".
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    Plugin.Logger.LogInfo("[Server] Initialising new game and loading character creation...");
                    // New() must be called first — same as MainMenuController.StartNewGame(difficulty).
                    // Without it, IntroCharacterCustomizer.StartGame() finds SaveGameManager.Current
                    // null and returns silently, making the Start button appear to do nothing.
                    // Discovery probe — logs GameVariables structure so we can
                    // later force multiplayer new games into Custom (non-story) mode.
                    SaveGameManager.New(BuildGameVariables(hostSettings));
                    LoadScene.LoadIntro(false);
                    Plugin.Logger.LogInfo("[Server] New game init + intro scene loaded.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Server] StartNewGame error: {ex}");
                }
            });
        }

        /// <summary>Round-285 backstop: a load that refuses AFTER StartLoadGame burned the
        /// lobby latch must hand the lobby back — without this, every such refusal wedged
        /// Start forever ("already in flight"; the pre-start validation normally prevents
        /// reaching this, so firing here means the in-load check caught a race the
        /// pre-check could not see).</summary>
        internal static void NotifyLoadRefused(string why)
        {
            if (!_running) return;
            // F15 (2026-08-21): a deep refusal consumes the dev impersonation too — same rule
            // as StartLoadGame's refusals (the override must never outlive its load).
            MPSaveCoordinator.ConsumeDevHostLoadAs("load refused (deep)");
            // 285 audit: restore the lobby ONLY when the lobby is what the failed start
            // consumed.  A refusal reached from IN-WORLD (the dev hostload verb; no retail
            // path leads here mid-session) must not flip lifecycle/UI to "lobby" under a
            // live world — MPState and MPLifecycle both key on IsInLobby.
            bool worldUp = false;
            try { worldUp = SaveGameManager.Current != null && Helpers.PlayerHelper.PlayerController != null; } catch { }
            if (worldUp)
            {
                Plugin.Logger.LogWarning($"[Server] load refused in-world — lobby latch untouched ({why}).");
                return;
            }
            IsInLobby = true;
            Plugin.Logger.LogWarning($"[Server] load refused after start — lobby restored ({why}).");
            try { MPCanvasUI.PostLobbyNotice($"Load refused: {why}"); } catch { }
        }

        /// <summary>Host clicked "Load Multiplayer Save" in the lobby.</summary>
        public static void StartLoadGame()
        {
            if (!_running) return;
            // PROTON-1: refuse to start a session this machine cannot write saves for -- an
            // unresolved version folder turns every MP store path relative, silently.
            MPSaveManager.EnsureVersionCached();
            if (string.IsNullOrEmpty(MPSaveManager.CachedVersionFolderOrEmpty))
            {
                if (!_versionRefusalLogged)
                {
                    _versionRefusalLogged = true;
                    Plugin.Logger.LogError("[MPSave] REFUSING to start: the game's save version folder cannot be resolved on this machine (PROTON-1)");
                }
                return;   // lobby untouched -- nothing has flipped yet
            }
            MPLoadProfiler.Mark($"HOST StartLoadGame (session='{ChosenLoadSession}') — {_clients.Count} client(s)");

            // Round-285: resolve AND validate the target session BEFORE any state flips.
            // The 2026-08-18 in-load precheck protects the MANIFEST, but by the time it
            // refused, this method had already burned IsInLobby — and nothing restores it
            // on refusal, so every Start click after that logged "already in flight"
            // forever (menu wedged until process restart; live 2026-08-21, twice).  A
            // refused load must leave the lobby exactly as it found it.
            string? mpSession = null;
            var sessions = MPSaveManager.ListSessions();
            // P2 (user-approved 2026-08-21): a PICKED save never substitutes. The old shape
            // fell through to "newest session of any world" when the picked name wasn't in
            // the loadable list — and with no loadable sessions at all, clean through to the
            // host's newest SINGLE-PLAYER save. Both silent wrong-world loads (audit F2/F3).
            if (!string.IsNullOrEmpty(ChosenLoadSession))
            {
                if (sessions.Exists(s => s.Name == ChosenLoadSession))
                {
                    mpSession = ChosenLoadSession;   // the picker's round-219 pin targets the exact world
                }
                else
                {
                    Plugin.Logger.LogWarning($"[Server] StartLoadGame: picked session '{ChosenLoadSession}' is not in the loadable list ({sessions.Count} candidate(s)) — refused, never substituted. Lobby unchanged.");
                    // E (2026-08-21): the accurate reason — a picked name absent from the
                    // loadable list means its CATALOG is missing/unreadable, not its character.
                    try { MPCanvasUI.PostLobbyNotice("Load refused: no save catalog found."); } catch { }
                    MPSaveCoordinator.ConsumeDevHostLoadAs("load refused");
                    return;   // IsInLobby untouched — the lobby stays live, Start stays clickable
                }
            }
            else if (sessions.Count > 0)
            {
                // Round-222: the legacy "resume newest" branch (NO explicit pick) chooses by
                // LIST position — carry that entry's identity so a duplicated name can't
                // resolve elsewhere. Only an unpicked start may resume-newest.
                mpSession = sessions[0].Name;
                try { MPSaveManager.NoteSessionPid(mpSession, sessions[0].Manifest?.PlaythroughId ?? ""); } catch { }
            }
            if (mpSession != null)
            {
                if (!MPSaveCoordinator.ValidateOwnSlotForLoad(mpSession, out string refuseWhy))
                {
                    Plugin.Logger.LogWarning($"[Server] StartLoadGame '{mpSession}' refused pre-start: {refuseWhy} Lobby unchanged.");
                    try { MPCanvasUI.PostLobbyNotice($"Load refused: {refuseWhy}"); } catch { }
                    // A refusal also CONSUMES any armed dev impersonation — tonight's trap was
                    // the override outliving its load and silently poisoning every later one.
                    MPSaveCoordinator.ConsumeDevHostLoadAs("load refused");
                    return;   // IsInLobby untouched — the lobby stays live, Start stays clickable
                }
            }

            IsInLobby = false;

            // Re-arm the startup pause hold for this new game.
            lock (_startupLock) { _inGamePlayers.Clear(); _worldReadyPlayers.Clear(); _fenceExcused.Clear(); _peerPhase.Clear(); _peerPhaseSeq.Clear(); _gateHeal.Clear(); _fenceArmedAtMs = TickMs64; _hostSnapshotsReady = false; _startupReleased = false; _pausedByDisconnect = false; _deliberatePause = false; }
            ClearV9SessionMaps();   // v9 review MIN-5: mirror memory, join baselines, applying latches die with every (re)arm
            lock (_joinBaselineDone) _joinBaselineDone.Clear();   // round-276: baseline latches die with the session
            _expectedLoadGen.Clear(); _parkedBaselineGen.Clear(); _firedLoadGen.Clear(); _markedInGame.Clear();   // round-284: load-ticket state too (the counter itself never resets — a reissued gen could match a stale echo)

            // Phase 4: if a multiplayer session exists, resume it — the host holds
            // every player's .hsg, so it ships each connected client its own and
            // loads its own, rather than everyone loading a single-player save.
            if (mpSession != null)
            {
                Plugin.Logger.LogInfo($"[Server] StartLoadGame → resuming MP session '{mpSession}'.");
                MPSaveCoordinator.HostLoadSession(mpSession);
                return;
            }

            // No MP session yet — fall back to the legacy "everyone loads their most
            // recent single-player save" behaviour.
            // Round-284: one shared ticket for the broadcast serve — every named peer
            // expects the same gen.
            var payload = new StartGamePayload { SaveName = "", LoadGen = MintSharedLoadGen() };
            Broadcast(MessageEnvelope.Create(MessageType.StartGameLoad, "host", payload));
            Plugin.Logger.LogInfo("[Server] StartLoadGame (legacy SP path) sent to all clients.");

            // Host loads its most recent save on the main thread
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    Plugin.Logger.LogInfo("[Server] Loading most recent save...");
                    var versionPath = SaveGamePathHelper.CurrentVersionFolderPath();
                    var saves = SaveGamePathHelper.GetAllSaveGamesFromVersion(versionPath);

                    if (saves == null || saves.Count == 0)
                    {
                        Plugin.Logger.LogWarning("[Server] No saves found — starting new game instead.");
                        SaveGameManager.New(MakeGameVariables());
                        LoadScene.LoadIntro(false);
                        return;
                    }

                    // Load the most recent save (list is sorted newest-first by the game)
                    var save = saves[0];
                    Plugin.Logger.LogInfo($"[Server] Loading save: {save.alias}");
                    MPSaveCoordinator.GuardedNativeLoad(save, true, "host latest-save fallback", save.alias);   // round-251
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Server] StartLoadGame error: {ex}");
                }
            });
        }

        // ── Events ────────────────────────────────────────────────────────────

        private static void OnPeerConnected(MPLink peer)
        {
            Plugin.Logger.LogInfo($"[Server] Peer connected: {peer.Id}");
            // Welcome message will be sent after we receive their Hello
            BillboardAdSync.NoteJoin();   // round-290: re-ship known campaign sets so the joiner converges
        }

        private static void OnPeerDisconnected(MPLink peer, string reason)
        {
            Plugin.Logger.LogInfo($"[Server] Peer disconnected: {peer.Id} — {reason}");
            _clients.TryRemove(peer, out _);
            lock (_pendingJoins) _pendingJoins.Remove(peer.Id);   // abandoned join request
            lock (_pendingSince) { _pendingSince.Remove(peer.Id); _pendingHbLines.Remove(peer.Id); }   // JOIN-WAIT-1
            // Round-281: drop the build record UNCONDITIONALLY (not inside the named-peer branch
            // below) — a transport can hand the same peer.Id to a LATER connection, and a stale
            // "cargo-delta capable" record inherited by a peer that never announced it is exactly
            // the version-skew hole this gate exists to close.
            _peerBuild.TryRemove(peer.Id, out _);
            _bldgByPeer.TryRemove(peer.Id, out _);   // T8: same recycled-id rule for the presence map

            // Clear any interior subscription the peer held so we stop polling
            // a building no client is in anymore.  Marshal — it mutates the same
            // subscriber dictionaries InteriorSync.Tick touches on the main thread.
            int gonePeerId = peer.Id;
            GameStatePatcher.EnqueueOnMainThread(() => InteriorSync.HandlePeerDisconnected(gonePeerId));
            // Review B1/MIN-7: peer ids are RECYCLED (the round-281 rule right above) — per-peer traffic
            // identity and the business-snapshot sig die with the connection. Marshalled: both maps are
            // main-thread-owned.
            GameStatePatcher.EnqueueOnMainThread(() => _lastBizSnapSig.Remove(gonePeerId));
            GameStatePatcher.EnqueueOnMainThread(() => _joinBizSigs.Remove(gonePeerId));   // v9: same recycled-id rule
            if (_peerNames.TryGetValue(peer.Id, out var goneTrafficPid) && !string.IsNullOrEmpty(goneTrafficPid))
            {
                ClearPlayerApplying(goneTrafficPid);   // v9: the applying latch is per-connection
                GameStatePatcher.EnqueueOnMainThread(() => TrafficSync.ForgetPeer(goneTrafficPid));
                // v9 review MAJOR-3: the parked delivery mirror and last-sent position are
                // per-connection state too — without this, a rejoiner's stale mirror suppresses
                // every displacement send until MarkPlayerInGame happens to fire.
                GameStatePatcher.EnqueueOnMainThread(() => ParkedVehicleSync.ForgetPeer(goneTrafficPid));
            }
            // v9 mirror memory is keyed by the client's STABLE id (not the recycled peer id) — the
            // record of what THIS connection was sent dies with it; a rejoiner re-receives once.
            if (_peerNames.TryGetValue(peer.Id, out var goneMirrorPid) && !string.IsNullOrEmpty(goneMirrorPid)
                && StableIdByPlayer.TryGetValue(goneMirrorPid, out var goneStable))
                ForgetMirrorMemory(goneStable);

            string? leftPlayer = null;

            // Remove from lobby player list
            if (_peerNames.TryGetValue(peer.Id, out leftPlayer))
            {
                // Round-253e: the mismatch record is ABOUT this player — it leaves with them
                // (the strip line derives from the dict, so it disappears the same frame).
                ModMismatchByPlayer.TryRemove(leftPlayer, out _);
                LobbyRemove(leftPlayer);
                try { _sharedPoolByOwner.Remove(leftPlayer); } catch { }   // shared-shop slice 3: an absent owner's bench is not replayed to anyone
                _peerNames.TryRemove(peer.Id, out _);
                BroadcastLobbyUpdate();   // keep everyone's roster (incl. the in-game F9 list) current
                if (!IsInLobby)
                    BroadcastPlayerLeft(leftPlayer); // also tell remaining clients to remove the capsule
                // Host's own duty map: a departed worker must not leave a
                // phantom "staffed" register (see HandlePlayerLeft client-side).
                var lp = leftPlayer;
                GameStatePatcher.EnqueueOnMainThread(() => { MPRegisterSync.RemovePlayer(lp); MPRestSync.RemovePlayer(lp); });   // duty + time-skip vote both die with the player
                GameStatePatcher.EnqueueOnMainThread(() => PruneOffers("'" + lp + "' left"));   // phase 1-A r4: the ONE validator; the RefreshGrantsAndBroadcast enqueued below carries the pruned table (r6: no second broadcast)
                GameStatePatcher.EnqueueOnMainThread(() => HostForgetPressesOf(lp));   // phase 4b (people) P4 r2 (MAJOR-3): a press waiting on a departed owner is refused, never left in flight
            }

            // A departed player drops out of every runtime grant relationship (their durable grants in
            // the store stay, to reactivate if they return).
            if (leftPlayer != null && _running)
                GameStatePatcher.EnqueueOnMainThread(RefreshGrantsAndBroadcast);

            // If a player disconnected during the startup hold, drop them from the
            // in-game set and re-check — the remaining players may now all be loaded.
            if (leftPlayer != null)
            {
                // Round-276: a leaver's baseline latch dies with them, so a rejoin
                // earns a fresh baseline even if its PlayerInGame races the phase flow.
                lock (_joinBaselineDone) _joinBaselineDone.Remove(leftPlayer);
                // Round-284: their load-ticket state dies with them too — a rejoin is a
                // fresh serve (new ticket), and a parked fire for a gone player must not
                // linger to fire on a stale echo after they return.
                _expectedLoadGen.TryRemove(leftPlayer, out _);
                _parkedBaselineGen.TryRemove(leftPlayer, out _);
                _firedLoadGen.TryRemove(leftPlayer, out _);
                _markedInGame.TryRemove(leftPlayer, out _);
                // Round-283: their phase freshness stamp dies with them.  A client that restarts
                // its process restarts its own counter at 1, so a last-seen figure that outlived
                // the connection would silently drop every report of the next one.
                _peerPhaseSeq.TryRemove(leftPlayer, out _);
                // Round-276b (verifier finding 2): their armed baseline VERIFY dies too —
                // a departed member's file can never land, and the surviving entry fell
                // straight through to a full refire for a player who is gone (F4's
                // congestion check reads 0 for a dead link, so it never shielded this).
                try { MPSaveCoordinator.DropJoinBaselineVerify(StableOfPid(leftPlayer)); } catch { }
                bool release = false;
                bool wasHeld = false;
                List<string> waiting = new();
                lock (_startupLock)
                {
                    if (!_startupReleased)
                    {
                        wasHeld = true;
                        _inGamePlayers.Remove(leftPlayer);
                        _worldReadyPlayers.Remove(leftPlayer);
                        // Release if the REMAINING players are all world-ready (the
                        // leaver no longer gates the hold).
                        waiting = LobbyPlayers.Where(p => !_worldReadyPlayers.Contains(p) && !_fenceExcused.Contains(p)).ToList();
                        if (LobbyPlayers.Count > 0 && waiting.Count == 0)
                        {
                            _startupReleased = true;
                            release = true;
                        }
                    }
                }
                if (release)      ReleaseStartupHold("remaining players loaded");
                else if (wasHeld) BroadcastStartupStatus(waiting);
            }

            // HOLD ownership for reconnect (Phase 4): a dropped player keeps their
            // buildings reserved to them so they reclaim everything on return,
            // rather than losing it.  We do NOT free/vacate them.  (Remaining
            // players see those buildings as owned, so they stay un-rentable.)
            int held = BuildingOwners.Count(kv => kv.Value == (leftPlayer ?? peer.Id.ToString()));
            if (held > 0)
                Plugin.Logger.LogInfo($"[Server] Holding {held} building(s) for '{leftPlayer ?? peer.Id.ToString()}' until reconnect.");

            // Remove the player's capsule from the host's own game
            if (leftPlayer != null)
                GameStatePatcher.EnqueueOnMainThread(() => RemotePlayerManager.Remove(leftPlayer));

            // Free the departed player's passenger seat AND eject anyone riding in a vehicle they owned
            // (else a phantom seat blocks boarding on an empty car + leaks into the join snapshot).
            // Broadcast a PassengerExit per freed rider so every client clears it too.
            if (leftPlayer != null)
            {
                string lpPass = leftPlayer;
                GameStatePatcher.EnqueueOnMainThread(() =>
                {
                    foreach (var fp in PassengerSync.HandlePlayerGone(lpPass))
                        Broadcast(MessageEnvelope.Create(MessageType.PassengerExit, MPConfig.PlayerId,
                            new PassengerExitPayload { PlayerId = fp, VehicleId = "" }));
                });
            }

            // In-game drop → always announce WHO left (the silent pause looked
            // like a freeze), save their state, and pause ONLY for timeout-style
            // drops (crash/network — a reconnect is plausible and supported).  A
            // clean close means the player deliberately quit: keep playing.
            if (leftPlayer != null && !IsInLobby)
            {
                // reason is the transport's DisconnectReason name (ToString) — same values as pre-seam.
                bool cleanLeave = reason == "RemoteConnectionClose"
                               || reason == "DisconnectPeerCalled";
                try
                {
                    string leftName = DisplayNameFor(leftPlayer);
                    string notice = cleanLeave
                        ? $"{leftName} left the game."
                        : $"{leftName} lost connection — game paused until they rejoin.";
                    MPChat.AddNotice(notice);          // host's own F9 chat
                    BroadcastChat("", "— " + notice);  // remaining clients' chat (no sender prefix)

                    if (!cleanLeave)
                    {
                        MPLog.Dump($"host: '{leftPlayer}' lost connection ({reason})");
                        _pausedByDisconnect = true;    // cleared on reconnect or overlay dismiss
                        DisconnectPauseWho  = leftPlayer;
                        BroadcastManualPause(true);
                        GameStatePatcher.EnqueueOnMainThread(() => TimeSync.SetManualPause(true));
                        Plugin.Logger.LogInfo($"[Server] Paused session — '{leftPlayer}' dropped ({reason}).");
                    }
                    else
                    {
                        Plugin.Logger.LogInfo($"[Server] '{leftPlayer}' left cleanly — game continues.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Disconnect handling: {ex.Message}"); }

                try { MPSaveCoordinator.HostSaveNow("disconnect"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Disconnect save: {ex.Message}"); }
            }
        }

        private static void OnReceive(MPLink peer, byte[] bytes)
        {
            MPNetStats.NoteIn(MPNetStats.PeekType(bytes), bytes.Length);   // T0 (review M8: torn frames count in bucket 0)
            var env   = MessageEnvelope.Deserialize(bytes);
            if (env == null) return;

            // ── Identity gate ─────────────────────────────────────────────────
            // _peerNames (bound once at Hello) is the only reliable source of WHO
            // sent a message.  Everything inside the envelope — env.SenderId and
            // any payload identity field — is client-authored and cannot be
            // relied on, so handlers key on senderPid and payload identity
            // claims are verified against it (SenderIs) before the host acts.
            if (!_peerNames.TryGetValue(peer.Id, out var senderPid)) senderPid = "";
            if (env.Type != MessageType.Hello && string.IsNullOrEmpty(senderPid))
            {
                Plugin.Logger.LogWarning($"[Server] {env.Type} from unregistered peer {peer.Id} — dropped.");
                return;
            }

            switch (env.Type)
            {
                case MessageType.Hello:
                    HandleHello(peer, env);
                    break;

                case MessageType.PlayerInGame:
                    HandleClientPlayerInGame(senderPid, env);
                    break;

                case MessageType.WorldReady:
                    HandleWorldReady(senderPid, env);
                    break;

                case MessageType.ManualPause:
                    HandleClientManualPause(peer, senderPid, env);
                    break;

                case MessageType.RentRequest:
                    HandleRentRequest(peer, senderPid, env);
                    break;

                case MessageType.VacateRequest:
                    HandleVacateRequest(peer, senderPid, env);
                    break;

                case MessageType.NativeClaim:
                    ContestedTenancy.HostHandleNativeClaim(senderPid, env.GetPayload<BuildingOwnershipPayload>());
                    break;
                case MessageType.BuyRequest:
                    HandleBuyRequest(peer, senderPid, env);
                    break;

                case MessageType.RadioState:            // round-227: relay a member's speaker change
                {
                    // Stage 3 review MINOR-X/Y: whole handler (validate + apply + relay) rides the
                    // main-thread queue — the apply touched Unity audio off-thread, and inline it
                    // could be overtaken by a QUEUED snapshot's older radio. A frame of relay
                    // latency on a human-initiated station change is nothing.
                    var rs = env.GetPayload<RadioStatePayload>();
                    if (rs != null) GameStatePatcher.EnqueueOnMainThread(() => MPRadioSync.HostHandle(rs, senderPid));
                    break;
                }

                case MessageType.BillboardAds:          // round-290: relay a member's billboard campaigns
                    BillboardAdSync.HostHandle(env.GetPayload<BillboardAdsPayload>(), senderPid, env);
                    break;

                case MessageType.TakeoverRequest:       // round-204b: client's AI-business offer — arbitrate on live data
                {
                    var tr = env.GetPayload<TakeoverPayload>();
                    var pidCapture = senderPid;
                    if (tr != null) GameStatePatcher.EnqueueOnMainThread(() => MPTakeover.HostHandleRequest(pidCapture, tr));
                    break;
                }

                case MessageType.ListForSale:
                    HandleListForSale(peer, senderPid, env);
                    break;

                case MessageType.CancelSale:
                    HandleCancelSale(peer, senderPid, env);
                    break;

                case MessageType.SaleCompleted:
                    HandleSaleCompleted(peer, senderPid, env);
                    break;

                case MessageType.PlayerMove:
                    HandlePlayerMove(peer, senderPid, env);
                    break;

                case MessageType.PlayerAnimTrigger:
                    HandleAnimTrigger(peer, senderPid, env);
                    break;

                case MessageType.VehicleSync:
                    HandleVehicleSync(peer, senderPid, env);
                    break;

                case MessageType.VehicleDrive:
                    HandleVehicleDrive(senderPid, env);
                    break;

                case MessageType.PassengerBoardRequest:
                    HandlePassengerBoardRequest(senderPid, env);
                    break;

                case MessageType.PassengerExit:
                    HandlePassengerExit(senderPid, env);
                    break;

                case MessageType.PassengerFollowRelayEnter:
                    HandlePassengerFollowRelayEnter(senderPid, env);
                    break;

                case MessageType.PassengerFollowRelayExit:
                    HandlePassengerFollowRelayExit(senderPid, env);
                    break;

                case MessageType.ClientDisconnectUpload:
                    HandleClientDisconnectUpload(senderPid, env);
                    break;

                case MessageType.PeerLogRequest:
                {
                    // Bug-report v2 (task #40): a client is filing a bug bundle — contribute the
                    // host's logs. Pure file IO + redaction → background thread; the reply routes
                    // back to the requester only.
                    var req = env.GetPayload<PeerLogRequestPayload>();
                    string reqPid = senderPid;
                    if (req != null && !string.IsNullOrEmpty(req.RequestId))
                        Task.Run(() => MPBugReport.RespondToPeerLogRequest(req.RequestId,
                            p => SendToPlayer(reqPid, MessageEnvelope.Create(MessageType.PeerLogReply, "host", p))));
                    break;
                }

                case MessageType.PeerLogReply:
                    // A client's log arriving for a bundle THIS machine is assembling.
                    MPBugReport.HandlePeerLogReply(env.GetPayload<PeerLogReplyPayload>());
                    break;

                case MessageType.GuestCargoGrab:
                    HandleGuestCargoGrab(senderPid, env.GetPayload<GuestCargoGrabPayload>());
                    break;

                case MessageType.JoinProgress:
                {
                    // Round-270: display-only — stash for the host's own overlay, rebroadcast
                    // so every player waiting on the startup screen sees the joiner's percent.
                    var jp = env.GetPayload<JoinProgressPayload>();
                    if (jp != null && !string.IsNullOrEmpty(jp.Pid))
                    {
                        MPCanvasUI.ReportJoinProgress(jp.Pid, jp.Percent);
                        Broadcast(env);
                    }
                    break;
                }

                case MessageType.VehicleLockSet:
                    HandleVehicleLockSet(senderPid, env);
                    break;

                case MessageType.PermissionGrantSet:
                    HandlePermissionGrantSet(senderPid, env);
                    break;

                case MessageType.MergerRequest:
                {
                    // Merger slice 1. Actor = the CONNECTION's pid (senderPid), never the payload —
                    // same spoof-guard as every relay gate.
                    var mreq = env.GetPayload<MergerRequestPayload>();
                    if (mreq != null)
                        GameStatePatcher.EnqueueOnMainThread(() => HostMergerAction(mreq.Action, mreq.TargetPid, senderPid));
                    break;
                }

                case MessageType.BusinessEditRequest:
                {
                    // Merger slice 3: routed owner-only edit — grant-gated, then applied or relayed.
                    var be = env.GetPayload<BusinessEditPayload>();
                    if (be != null)
                        GameStatePatcher.EnqueueOnMainThread(() => BusinessSync.HostRouteBusinessEdit(be, senderPid));
                    break;
                }

                case MessageType.MergerWalletDelta:
                {
                    // Merger slice 4: a member's native money delta → the host ledger.
                    var wd = env.GetPayload<MergerWalletDeltaPayload>();
                    if (wd != null && SenderIs(wd.PlayerId, senderPid, MessageType.MergerWalletDelta))
                        GameStatePatcher.EnqueueOnMainThread(() =>
                        {
                            HostWalletDelta(wd.PlayerId, wd.Amount, wd.Key, wd.Contribution);
                            CompanyFeed.HostIngest(wd.Tx, wd.PlayerId);   // PHASE 4b (T1): the transaction record rides the wallet forward
                        });
                    break;
                }

                case MessageType.MergerEmployeeEdit:
                {
                    // Merger slice 5: routed fire / schedule write-back — gate, then apply or relay.
                    var ee = env.GetPayload<EmployeeEditPayload>();
                    if (ee != null && SenderIs(ee.PlayerId, senderPid, MessageType.MergerEmployeeEdit))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteEmployeeEdit(ee, senderPid));
                    break;
                }

                case MessageType.NotificationRelay:
                {
                    // Merger slice 6: a member's business-scoped pop-up — fan it out inside THAT member's group.
                    var nr = env.GetPayload<NotificationRelayPayload>();
                    if (nr != null && SenderIs(nr.PlayerId, senderPid, MessageType.NotificationRelay))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRelayNotification(nr, senderPid));
                    break;
                }

                case MessageType.MergerHandover:
                {
                    // MERGER PHASE 3-B: the ONLY direction a client sends this type is the designated
                    // simulator's ACK of a hand-over. Nothing is applied here - the ack is a log line
                    // (D15: the host holds the pushes regardless, so a lost ack costs nothing).
                    // r4 F3: the same client -> host direction also carries the ONE request a simulator
                    // can make - Ack="resend" ("my scene reloaded, my installs are gone, hand it to me
                    // again"). Nothing was added to the payload for it: this type already travelled this
                    // way carrying nothing but Ack. P3-C r2 (F4) adds the second: Ack="return-applied",
                    // the RETURNED OWNER confirming the return leg landed - the one thing that clears the
                    // mark (the host's send only flags it ReturnSent).
                    var hv = env.GetPayload<MergerHandoverPayload>();
                    if (hv == null) break;
                    if (hv.Ack == "return-applied")
                    {
                        // MERGER PHASE 3-C r2 (F4): the OWNER acked the RETURN leg. THIS - never the send -
                        // is what clears the mark, so a return lost to a drop during the paced interior
                        // send re-fires at that owner's next load instead of vanishing while their stale
                        // publish overwrites the host's simulated record.
                        string ackPid = senderPid;
                        GameStatePatcher.EnqueueOnMainThread(() => HostReturnAck(ackPid));
                    }
                    else if (hv.Ack == "resend")
                    {
                        string askPid = senderPid, askStable = hv.OwnerStable ?? "";
                        GameStatePatcher.EnqueueOnMainThread(() => HostResendHandover(askPid, askStable));
                    }
                    else if (!string.IsNullOrEmpty(hv.Ack))
                        Plugin.Logger.LogInfo($"[Absence] '{senderPid}' acked the hand-over of '{hv.OwnerPid}': {hv.Ack}.");
                    break;
                }

                case MessageType.CompanyBooks:
                {
                    // MERGER PHASE 4a (D14): one member's own daily books. Client -> host; the host
                    // stores them per owner, re-keys anything the sender only SIMULATES to the real
                    // owner, and fans the result out to that owner's online co-members.
                    var cb = env.GetPayload<CompanyBooksPayload>();
                    if (cb != null && SenderIs(cb.OwnerPid, senderPid, MessageType.CompanyBooks))
                        GameStatePatcher.EnqueueOnMainThread(() => CompanyBooks.HostStore(cb, senderPid));
                    break;
                }

                case MessageType.MergerTax:
                {
                    // MERGER PHASE 4a / B9: the tax pay-all ("payall") or one partner's result
                    // ("report"). The host never receives "payown" - that direction is host -> member.
                    var mt = env.GetPayload<MergerTaxPayload>();
                    if (mt == null) break;
                    if (!SenderIs(mt.PlayerId, senderPid, MessageType.MergerTax)) break;
                    string tPid = senderPid;
                    if (mt.Action == "payall")      GameStatePatcher.EnqueueOnMainThread(() => CompanyBooks.HostPayAll(mt, tPid));
                    else if (mt.Action == "report") GameStatePatcher.EnqueueOnMainThread(() => CompanyBooks.HostReport(mt, tPid));
                    else Plugin.Logger.LogWarning($"[Tax] refused a MergerTax from '{tPid}': action '{mt.Action}' is not one this direction carries.");
                    break;
                }

                case MessageType.BusinessPaperwork:
                {
                    // Merger phase 3-A: one member's paperwork bundle. Client -> host ONLY; the host
                    // never sends this type. Stored per member (latest wins) and written into the
                    // session manifest at the next coordinated save - no gameplay effect in P3-A.
                    var pw = env.GetPayload<BusinessPaperworkPayload>();
                    if (pw != null && SenderIs(pw.PlayerId, senderPid, MessageType.BusinessPaperwork))
                        GameStatePatcher.EnqueueOnMainThread(() => StorePaperwork(pw, senderPid));
                    break;
                }

                // ── Shared-shop management (Business PERMISSION feature) — separate from the merger cases above ──
                case MessageType.SharedScheduleEdit:
                {
                    var se = env.GetPayload<SharedScheduleEditPayload>();
                    if (se != null && SenderIs(se.PlayerId, senderPid, MessageType.SharedScheduleEdit))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteSharedScheduleEdit(se, senderPid));
                    break;
                }
                case MessageType.ScheduleSession:
                {
                    var ss = env.GetPayload<ScheduleSessionPayload>();
                    if (ss != null && SenderIs(ss.PlayerId, senderPid, MessageType.ScheduleSession))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteScheduleSession(ss, senderPid));
                    break;
                }
                case MessageType.SharedStaffPool:
                {
                    var sp = env.GetPayload<SharedStaffPoolPayload>();
                    if (sp != null && SenderIs(sp.PlayerId, senderPid, MessageType.SharedStaffPool))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteSharedStaffPool(sp, senderPid));
                    break;
                }
                case MessageType.SharedStaffEdit:
                {
                    var sf = env.GetPayload<SharedStaffEditPayload>();
                    if (sf != null && SenderIs(sf.PlayerId, senderPid, MessageType.SharedStaffEdit))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteSharedStaffEdit(sf, senderPid));
                    break;
                }
                case MessageType.CompanyCandidates:
                {
                    // Merger phase 4b (people) part 1: a member's candidate pool, or a claim/release on
                    // one of them, or "somebody hired out of your pool". Main thread - the pool leg
                    // reads the sender's rows and every leg may write the host's own candidate list.
                    var cc = env.GetPayload<CompanyCandidatesPayload>();
                    if (cc != null && SenderIs(cc.PlayerId, senderPid, MessageType.CompanyCandidates))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteCompanyCandidates(cc, senderPid));
                    break;
                }
                case MessageType.CompanyMessages:
                {
                    // Merger phase 4b (people) P4: one member's business/staff MESSAGE for its co-members,
                    // a co-member's button PRESS for the owner, or the owner's HANDLED mark. Main thread -
                    // the host is a member too, so every leg may write this save's own contacts.
                    var cm = env.GetPayload<CompanyMessagePayload>();
                    if (cm != null && SenderIs(cm.PlayerId, senderPid, MessageType.CompanyMessages))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteCompanyMessages(cm, senderPid));
                    break;
                }
                case MessageType.CargoTransfer:
                {
                    // Merger phase 4c part 2: one leg of a routed cargo transfer - the need ask or its
                    // answer, the source's offer of what it withdrew, or the destination's ack. Main
                    // thread - the host is a member too, so a leg addressed to this machine's own
                    // buildings is applied here and writes this save's stock.
                    var ct = env.GetPayload<CargoTransferPayload>();
                    if (ct != null && SenderIs(ct.PlayerId, senderPid, MessageType.CargoTransfer))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteCargoTransfer(ct, senderPid));
                    break;
                }
                case MessageType.SharedPriceEdit:
                {
                    var pe = env.GetPayload<SharedPriceEditPayload>();
                    if (pe != null && SenderIs(pe.PlayerId, senderPid, MessageType.SharedPriceEdit))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteSharedPriceEdit(pe, senderPid));
                    break;
                }
                case MessageType.SharedSalesHistory:
                {
                    var sh = env.GetPayload<SharedSalesHistoryPayload>();
                    if (sh != null && SenderIs(sh.PlayerId, senderPid, MessageType.SharedSalesHistory))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteSharedSalesHistory(sh, senderPid));
                    break;
                }
                case MessageType.ShopValuation:   // H-BIZ-1: "request" → the shop's owner; "answer" → the one viewer that asked
                {
                    var sv = env.GetPayload<ShopValuationPayload>();
                    if (sv != null && SenderIs(sv.PlayerId, senderPid, MessageType.ShopValuation))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteShopValuation(sv, senderPid));
                    break;
                }
                case MessageType.RivalStaffReq:   // AI-staff slice (2026-08-29): host answers from its authoritative list
                {
                    var rq = env.GetPayload<RivalStaffReqPayload>();
                    if (rq != null) GameStatePatcher.EnqueueOnMainThread(() => RivalStaffSync.HostAnswerStaffReq(rq, senderPid));
                    break;
                }
                case MessageType.PoachClaim:      // AI-staff slice: the hire is arbitrated here
                {
                    var pc = env.GetPayload<PoachClaimPayload>();
                    if (pc != null) GameStatePatcher.EnqueueOnMainThread(() => RivalStaffSync.HostHandlePoachClaim(pc, senderPid));
                    break;
                }

                case MessageType.SharedWorkInfo:
                {
                    var wi = env.GetPayload<SharedWorkInfoPayload>();
                    if (wi != null && SenderIs(wi.PlayerId, senderPid, MessageType.SharedWorkInfo))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteSharedWorkInfo(wi, senderPid));
                    break;
                }

                case MessageType.SharedWorkEdit:
                {
                    var we = env.GetPayload<SharedWorkEditPayload>();
                    if (we != null && SenderIs(we.PlayerId, senderPid, MessageType.SharedWorkEdit))
                        GameStatePatcher.EnqueueOnMainThread(() => HostRouteSharedWorkEdit(we, senderPid));
                    break;
                }

                case MessageType.TaxiHail:
                    HandleTaxiHail(env);
                    break;

                case MessageType.TrafficModeAck:
                {
                    // TRAFFIC-APART P4: the sender is the connection's VERIFIED identity (bound at Hello) - the
                    // payload names no player, so there is nothing here to spoof and nothing for SenderIs to check.
                    var tma = env.GetPayload<TrafficModeAckPayload>();
                    if (tma != null)
                        GameStatePatcher.EnqueueOnMainThread(() => TrafficSync.HostOnModeAck(senderPid, tma));
                    break;
                }

                case MessageType.ClientTrafficSnapshot:
                {
                    // TRAFFIC-CONSIST T1: the sender is the connection's VERIFIED identity (bound at Hello) - the
                    // payload names no player, so there is nothing here to spoof, exactly as for TrafficModeAck.
                    // Main thread: the handler spawns and moves the stand-in objects.
                    var cts = env.GetPayload<ClientTrafficSnapshotPayload>();
                    if (cts != null)
                        GameStatePatcher.EnqueueOnMainThread(() => TrafficSync.HostOnClientTrafficSnapshot(senderPid, cts));
                    break;
                }

                case MessageType.PlayerAppearance:
                    HandleClientAppearance(senderPid, env);
                    break;

                case MessageType.InteriorRequest:
                {
                    // HandleRequest builds the interior snapshot (reads IL2CPP:
                    // reg.interiorDesigns/itemInstances/dirtSpots) AND mutates the
                    // subscriber dictionaries that InteriorSync.Tick also touches on
                    // the main thread — so run it ON the main thread (off-thread
                    // IL2CPP read + concurrent-dictionary race otherwise).
                    var p = env.GetPayload<InteriorRequestPayload>();
                    if (p != null && SenderIs(p.PlayerId, senderPid, env.Type))
                    {
                        var pc = peer;
                        // D1: Ambient=true is a viewer NEAR the building (a Hamptons house at LOD0),
                        // not one inside it — the host keeps that membership apart from the entry sub.
                        bool ambientReq = p.Ambient;
                        GameStatePatcher.EnqueueOnMainThread(() => InteriorSync.HandleRequest(pc, p.PlayerId, p.AddressKey, ambientReq));
                    }
                    break;
                }

                case MessageType.InteriorOwnerSnapshot:
                {
                    var p = env.GetPayload<InteriorSnapshotPayload>();
                    if (p != null)
                    {
                        var pc = peer;
                        GameStatePatcher.EnqueueOnMainThread(() => InteriorSync.HandleOwnerSnapshot(pc, senderPid, p));
                    }
                    break;
                }

                case MessageType.InteriorCargoSync:
                {
                    // v10 (T7): owner → host cargo-only upload (ownership + structure-hash
                    // checked in the handler; main thread — it mutates the owner cache).
                    var p = env.GetPayload<InteriorCargoSyncPayload>();
                    if (p != null)
                    {
                        var pc = peer;
                        GameStatePatcher.EnqueueOnMainThread(() => InteriorSync.HandleOwnerCargoSync(pc, senderPid, p));
                    }
                    break;
                }

                case MessageType.InteriorDirtSync:
                {
                    // v10 (T7/ruling 33): owner → host dirt-values upload.
                    var p = env.GetPayload<InteriorDirtSyncPayload>();
                    if (p != null)
                    {
                        var pc = peer;
                        GameStatePatcher.EnqueueOnMainThread(() => InteriorSync.HandleOwnerDirtSync(pc, senderPid, p));
                    }
                    break;
                }

                case MessageType.PlayerExitedBuilding:
                {
                    // Mutates the same subscriber dictionaries as Tick (main thread)
                    // — marshal to avoid the concurrent-dictionary race.
                    var p = env.GetPayload<PlayerExitedBuildingPayload>();
                    if (p != null && SenderIs(p.PlayerId, senderPid, env.Type))
                    {
                        var pc = peer;
                        bool ambientExit = p.Ambient;
                        GameStatePatcher.EnqueueOnMainThread(() => InteriorSync.HandleExit(pc, p.PlayerId, p.AddressKey, ambientExit));
                        // D1: an AMBIENT exit is NOT a player leaving a building — it is a house
                        // dropping out of their LOD0 range while they stand in the street. None of the
                        // presence work behind a real exit may run for it.
                        if (!ambientExit)
                        {
                            ParkedVehicleSync.ForgetPeer(p.PlayerId);   // door teleport — resync their parked cars now
                            TrafficSync.ForgetPeer(p.PlayerId);         // review B1: same rule for the traffic identity map
                        }
                    }
                    break;
                }

                case MessageType.RivalsStatsRequest:
                {
                    // CRITICAL: PollLoop runs on a BACKGROUND THREAD.  Building
                    // the stats snapshot iterates IL2CPP-Interop objects
                    // (gi.BuildingRegistrations, RivalData.WeeklyIncome,
                    // ownedBusinesses lists) which is unsafe from non-main
                    // threads — eventually native-crashes.  Marshal the work
                    // onto Unity's main thread via EnqueueOnMainThread.
                    // Self-stats capture (pure C# dict writes) is safe inline.
                    var req = env.GetPayload<RivalsStatsRequestPayload>();
                    if (req != null && !string.IsNullOrEmpty(req.PlayerId)
                        && SenderIs(req.PlayerId, senderPid, env.Type))
                    {
                        _clientSelfStats[req.PlayerId] = req;
                        string nameForClient = _characterNamesByPlayerId.TryGetValue(req.PlayerId, out var nm) && !string.IsNullOrWhiteSpace(nm) ? nm : req.PlayerId;
                        var selfInfo = new RivalStatsInfo
                        {
                            Id                     = req.PlayerId,
                            Name                   = nameForClient,
                            OwnedBuildingsCount    = req.SelfOwnedBuildingsCount,
                            OwnedBusinessesCount   = req.SelfOwnedBusinessesCount,
                            WeeklyIncome           = req.SelfWeeklyIncome,
                            MostActiveNeighborhood = req.SelfNeighborhood ?? "",
                        };
                        // Round-25: carry the rows + series into the HOST's OWN UI caches too — without them
                        // the host's detail view of this player fell back to replica regs (no order history)
                        // and showed $0 per-business rows.
                        if (req.Businesses      != null && req.Businesses.Count      > 0) selfInfo.Businesses      = req.Businesses;
                        if (req.IncomeHistory   != null && req.IncomeHistory.Count   > 0) selfInfo.IncomeHistory   = req.IncomeHistory;
                        if (req.BizCountHistory != null && req.BizCountHistory.Count > 0) selfInfo.BizCountHistory = req.BizCountHistory;
                        GameStatePatcher.ClientRivalStats[req.PlayerId] = selfInfo;
                        Plugin.Logger.LogInfo($"[Server] Stored self-stats from '{req.PlayerId}': bldgs={req.SelfOwnedBuildingsCount} biz={req.SelfOwnedBusinessesCount} income=${req.SelfWeeklyIncome:F0} hood='{req.SelfNeighborhood}'.");
                    }
                    var reqCapture = req;
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        try
                        {
                            // Plain-Dictionary — main-thread writes only. Feeds the host's breakdown-cell
                            // override so the host renders this player's per-business income (round-25).
                            if (reqCapture?.Businesses != null)
                                foreach (var b in reqCapture.Businesses)
                                    if (b != null && !string.IsNullOrEmpty(b.AddressKey))
                                        GameStatePatcher.ClientBusinessIncomeByAddress[b.AddressKey] = b.WeeklyIncome;
                            // Round-25 freshness: a self-report changes what EVERY viewer should see —
                            // broadcast the merged snapshot to all peers, not just the requester.
                            BroadcastRivalsStatsSnapshot();
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] RivalsStatsRequest main-thread dispatch: {ex.Message}"); }
                    });
                    break;
                }

                case MessageType.PlayerProfile:
                {
                    var p = env.GetPayload<PlayerProfilePayload>();
                    if (p != null && !string.IsNullOrEmpty(p.PlayerId)
                        && SenderIs(p.PlayerId, senderPid, env.Type))
                    {
                        _characterNamesByPlayerId[p.PlayerId] = p.CharacterName ?? "";
                        if (p.AgeInYears > 0) GameStatePatcher.ClientPlayerAges[p.PlayerId] = p.AgeInYears;
                if (p.Gender >= 0) GameStatePatcher.ClientPlayerGenders[p.PlayerId] = p.Gender;
                        string resolvedName = string.IsNullOrWhiteSpace(p.CharacterName) ? p.PlayerId : p.CharacterName;
                        // Populate HOST's local caches so host's own UI can
                        // render this player as a rival.  Mirrors what the
                        // client does on its side via EnsureRivalCachesPopulated.
                        GameStatePatcher.ClientRivalNames[p.PlayerId] = resolvedName;
                        // Add to player roster (drives Patch_Load_AddPlayers
                        // injection of leaderboard rows for this player).
                        if (p.PlayerId != MPConfig.PlayerId)
                            GameStatePatcher.ClientPlayerRoster[p.PlayerId] = resolvedName;
                        Plugin.Logger.LogInfo($"[Server] PlayerProfile from peer={peer.Id}: PlayerId='{p.PlayerId}' CharacterName='{p.CharacterName}'.  Re-broadcasting roster.");
                        // Forward to all so every client (including the sender) sees the
                        // updated mapping.  Pure byte-forward — always safe.
                        p.ColourSlot = PlayerColours.SlotOf(p.PlayerId);   // 2026-09-05 colours: the host names the sender's slot on the way out
                        Broadcast(MessageEnvelope.Create(MessageType.PlayerProfile, "host", p));

                        // Everything below — populating gi.rivalStates, decoding the
                        // portrait into a Texture2D/Sprite, and BuildRivalsSnapshot —
                        // touches IL2CPP game systems that DO NOT EXIST while the HOST is
                        // still in character creation / the intro scene.  Running them
                        // there NATIVE-CRASHES the host (uncatchable) when a client loads
                        // far ahead of it.  The name/roster were already stored above
                        // (pure C#), and the host re-broadcasts full rivals state on
                        // go-live, so deferring this costs nothing but a transient
                        // portrait.  Gate on the host being truly in the game world.
                        if (!HostSnapshotsReady)
                        {
                            Plugin.Logger.LogInfo($"[Server] PlayerProfile '{p.PlayerId}': host not in-world yet — deferred rivals/portrait/snapshot work (avoids intro-scene crash).");
                        }
                        else
                        {
                            GameStatePatcher.EnqueueOnMainThread(() =>
                            {
                                try
                                {
                                    // Mark IsPlayer=true so EnsureRivalCachesPopulated
                                    // skips adding this id to gi.rivalStates (which
                                    // would create a ghost leaderboard row).  Player
                                    // rows come solely from Patch_Load_AddPlayers.
                                    var injected = new RivalsSnapshotPayload();
                                    injected.Rivals.Add(new RivalInfo { Id = p.PlayerId, Name = resolvedName, IsPlayer = true });
                                    GameStatePatcher.EnsureRivalCachesPopulated(injected);
                                    // Decode this client's portrait so the HOST'S rivals
                                    // profile shows the client's real face.
                                    if (!string.IsNullOrEmpty(p.PortraitPngBase64))
                                        GameStatePatcher.ApplyPlayerPortrait(p.PlayerId, p.PortraitPngBase64);
                                }
                                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Local rival cache add for client: {ex.Message}"); }
                            });
                            try
                            {
                                var snap = BuildRivalsSnapshot();
                                Broadcast(MessageEnvelope.Create(MessageType.RivalsSnapshot, "host", snap));
                                Plugin.Logger.LogInfo($"[Server] Re-broadcast rivals snapshot (profile update): {snap.Rivals.Count} rival(s).");
                            }
                            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Rivals re-broadcast on profile: {ex.Message}"); }
                        }
                    }
                    break;
                }

                case MessageType.MirrorAck:
                    // v9: mirror delivery confirmation — dict write, safe on the poll thread.
                    RecordMirrorAck(senderPid, env.GetPayload<MirrorAckPayload>());
                    break;

                case MessageType.SaveData:
                {
                    // Pure C# decompress + file write + manifest merge (no IL2CPP)
                    // — safe on the poll thread.  The slot's StableId names the
                    // on-disk character folder — it must be the SENDER's stable
                    // id, or one client could overwrite another player's save.
                    var sd = env.GetPayload<SaveDataPayload>();
                    if (sd?.Slot == null) break;
                    sd.HsgRaw = env.Attachment;   // v9 rider → payload
                    if (!StableIdByPlayer.TryGetValue(senderPid, out var expectStable)
                        || string.IsNullOrEmpty(expectStable) || sd.Slot.StableId != expectStable)
                    {
                        Plugin.Logger.LogWarning($"[Server] SaveData from '{senderPid}' claims stableId '{sd.Slot.StableId}' (expected '{expectStable}') — dropped.");
                        break;
                    }
                    MPSaveCoordinator.HostHandleSaveData(sd);
                    break;
                }

                case MessageType.BusinessChange:
                {
                    // A client built/changed a business in a building it owns.  Apply it
                    // to the host's world so the host sees it; the host's BusinessSync.Tick
                    // then relays it to the other clients.  Ownership rule: the sender must
                    // OWN this address in a ledger (rented or bought); AI/unowned addresses are
                    // rejected too (closes the forge-onto-AI-shop hole).  A change that lands in
                    // the brief just-rented window before ownership is recorded is re-sent by the
                    // owner's periodic re-assert (BusinessSync.TickClient) and accepted then.
                    var bc = env.GetPayload<BusinessChangePayload>();
                    if (bc?.Info == null) break;
                    if (!SenderIs(bc.Info.OwnerPlayerId, senderPid, env.Type, allowEmpty: true)) break;
                    if (!SenderOwns(bc.Info.AddressKey ?? "", senderPid))
                    {
                        LogRejectThrottled("BusinessChange", bc.Info.AddressKey, $"from '{senderPid}' — sender doesn't own it (incl. AI/unowned)");
                        // Round-61: unless this is a LEDGER HOLE — the owner's machine runs a
                        // living business the host's ledger knows nothing about. See the method.
                        TryAdoptOrphanedTenancy(bc.Info, senderPid);
                        break;
                    }
                    GameStatePatcher.ApplyClientBusinessChange(bc.Info);
                    break;
                }

                case MessageType.LobbyPref:
                {
                    var lp = env.GetPayload<LobbyPrefPayload>();
                    if (lp == null || !SenderIs(lp.PlayerId, senderPid, env.Type)) break;
                    if (lp.Age < 16 || lp.Age > 79)   // round-226: above 79 = native zombie-walk (80) / death (90) territory
                    {
                        Plugin.Logger.LogWarning($"[Server] LobbyPref from '{senderPid}': age {lp.Age} out of range — dropped.");
                        break;
                    }
                    SetStartingAge(senderPid, lp.Age);   // stores + re-broadcasts lobby
                    break;
                }

                case MessageType.LoanOffer:
                {
                    // Hub offers (gift/loan): only the sender's OWN offers and
                    // revokes are accepted ("accepted"/"declined" are host-authored
                    // result states — a client never legitimately sends them), the
                    // target must be a real other player, and the figures must be sane.
                    var lo = env.GetPayload<LoanOfferPayload>();
                    if (lo == null || !SenderIs(lo.From, senderPid, env.Type)) break;
                    if (lo.State != "offer" && lo.State != "revoke")
                    {
                        Plugin.Logger.LogWarning($"[Server] LoanOffer from '{senderPid}' with host-only state '{lo.State}' — dropped.");
                        break;
                    }
                    if (lo.State == "offer")
                    {
                        if (!IsSaneMoney(lo.Principal) || lo.Principal <= 0f
                            || !IsSaneMoney(lo.DailyInterest) || lo.DailyInterest < 0f
                            || !IsSaneMoney(lo.DailyPayment) || lo.DailyPayment < 0f)
                        {
                            Plugin.Logger.LogWarning($"[Server] LoanOffer from '{senderPid}': implausible figures (P={lo.Principal}, i={lo.DailyInterest}, pay={lo.DailyPayment}) — dropped.");
                            break;
                        }
                        if (lo.To == senderPid || !LobbyPlayers.Contains(lo.To))
                        {
                            Plugin.Logger.LogWarning($"[Server] LoanOffer from '{senderPid}' to invalid target '{lo.To}' — dropped.");
                            break;
                        }
                    }
                    GameStatePatcher.EnqueueOnMainThread(() => MPHub.HostRouteOffer(lo));
                    break;
                }

                case MessageType.LoanAnswer:
                {
                    var la = env.GetPayload<LoanAnswerPayload>();
                    if (la == null || !SenderIs(la.From, senderPid, env.Type)) break;
                    GameStatePatcher.EnqueueOnMainThread(() => MPHub.HostHandleAnswer(la));
                    break;
                }

                case MessageType.LoanRepay:
                {
                    var lr = env.GetPayload<LoanRepayPayload>();
                    if (lr == null || !SenderIs(lr.From, senderPid, env.Type)) break;
                    GameStatePatcher.EnqueueOnMainThread(() => MPHub.HostHandleRepay(lr));
                    break;
                }

                case MessageType.BizTransferAck:   // round-196: buyer's local claim completed
                {
                    var bt = env.GetPayload<BizTransferPayload>();
                    if (bt == null) break;
                    GameStatePatcher.EnqueueOnMainThread(() => MPOffers.HostHandleAck(bt, senderPid));
                    break;
                }

                case MessageType.RestVote:
                {
                    var rv = env.GetPayload<RestVotePayload>();
                    if (rv == null || !SenderIs(rv.PlayerId, senderPid, env.Type)) break;
                    // An invalid goal (NaN/out-of-range) would skew the consensus
                    // minimum every other player skips to.
                    if (double.IsNaN(rv.GoalMinutes) || double.IsInfinity(rv.GoalMinutes)
                        || rv.GoalMinutes < 0 || rv.GoalMinutes > 10_000_000)
                    {
                        Plugin.Logger.LogWarning($"[Server] RestVote from '{senderPid}': implausible goal {rv.GoalMinutes} — dropped.");
                        break;
                    }
                    GameStatePatcher.EnqueueOnMainThread(() => MPRestSync.HostHandleVote(rv));
                    break;
                }

                case MessageType.BuildingDirtEdit:
                {
                    // Round-112: a helper mopped a floor in someone else's business.  Routing + grant check
                    // live in HandleBuildingDirtEdit (shared with the host's own local path).
                    var de = env.GetPayload<DirtEditPayload>();
                    if (de == null || string.IsNullOrEmpty(de.AddressKey)) break;
                    if (!SenderIs(de.SenderId, senderPid, env.Type)) break;
                    if (de.Spots.Count > 2000)
                    {
                        Plugin.Logger.LogWarning($"[Cleaning] dirt edit for '{de.AddressKey}' from '{senderPid}': implausible size ({de.Spots.Count} cells) — dropped.");
                        break;
                    }
                    HandleBuildingDirtEdit(de, senderPid);
                    break;
                }

                case MessageType.RetailPrices:
                {
                    // A client's business changed its retail prices: apply to the
                    // host's local registration copy (main thread) + relay to all
                    // (the sender's own echo is dropped by the OwnerId guard).
                    // Ownership rule (same as BusinessChange): the sender must OWN this
                    // address in a ledger (rented or bought); AI/unowned addresses are rejected
                    // too.  A reprice that lands in the brief just-rented window before ownership
                    // is recorded is re-sent by the owner's periodic re-assert (MPPriceSync.Tick).
                    var rp = env.GetPayload<RetailPricesPayload>();
                    if (rp == null || string.IsNullOrEmpty(rp.AddressKey)) break;
                    if (!SenderIs(rp.OwnerId, senderPid, env.Type)) break;
                    if (!SenderOwns(rp.AddressKey, senderPid))
                    {
                        LogRejectThrottled("RetailPrices", rp.AddressKey, $"from '{senderPid}' — sender doesn't own it (incl. AI/unowned)");
                        break;
                    }
                    if (rp.Prices.Count > 500
                        || rp.Prices.Exists(x => !IsSaneMoney(x.Price, 1_000_000f) || x.Price < 0f))
                    {
                        Plugin.Logger.LogWarning($"[Server] RetailPrices for '{rp.AddressKey}' from '{senderPid}': implausible price table ({rp.Prices.Count} entries) — dropped.");
                        break;
                    }
                    GameStatePatcher.EnqueueOnMainThread(() => MPPriceSync.Apply(rp));
                    BroadcastRetailPrices(rp);   // relay to all + cache for join replay (Class 4)
                    break;
                }

                case MessageType.ShopStockDigest:
                {
                    var sd = env.GetPayload<ShopStockDigestPayload>();
                    if (sd != null && SenderOwns(sd.AddressKey, senderPid))   // only the shop's owner may report its stock
                    {
                        GameStatePatcher.EnqueueOnMainThread(() => MPStockSync.Apply(sd));
                        BroadcastStockDigest(sd);   // relay to everyone (the owner's own echo is idempotent)
                    }
                    break;
                }

                case MessageType.AuditReport:
                {
                    // Client's periodic state-hash audit — compare to OUR state
                    // on the main thread (BuildReport walks game objects).
                    // Release-enabled 2026-06-24: divergence now surfaces in users'
                    // bug-report logs, not just Dev testing.
                    var ar = env.GetPayload<AuditReportPayload>();
                    if (ar != null && SenderIs(ar.PlayerId, senderPid, env.Type))
                        GameStatePatcher.EnqueueOnMainThread(() => MPAudit.HostHandle(ar));
                    break;
                }

                case MessageType.AuditDrillReply:
                {
                    // Round-89: the client's per-reg drill hashes — diff on the main
                    // thread and NAME the diverging address(es) in the host log.
                    var dr = env.GetPayload<AuditDrillReplyPayload>();
                    if (dr != null && SenderIs(dr.PlayerId, senderPid, env.Type))
                        GameStatePatcher.EnqueueOnMainThread(() => MPAudit.HostHandleDrillReply(dr));
                    break;
                }

                case MessageType.Chat:
                {
                    // A client chatted.  PUBLIC → append + relay to everyone (the
                    // sender included — host-ordered echo).  PRIVATE → deliver to
                    // the recipient ONLY (the sender already echoed locally).
                    // Pure C# — safe on the poll thread.
                    var cp = env.GetPayload<ChatPayload>();
                    if (cp != null && !string.IsNullOrWhiteSpace(cp.Text)
                        && SenderIs(cp.PlayerId, senderPid, env.Type))
                    {
                        if (cp.Text.Length > 1000) cp.Text = cp.Text.Substring(0, 1000);
                        if (string.IsNullOrEmpty(cp.To))
                        {
                            MPChat.AddMessage(cp.PlayerId, "", cp.Text);
                            Broadcast(MessageEnvelope.Create(MessageType.Chat, "host", cp));
                        }
                        else if (cp.To == MPConfig.PlayerId)
                        {
                            MPChat.AddMessage(cp.PlayerId, cp.To, cp.Text);   // private to the host
                        }
                        else
                        {
                            SendChatPrivate(cp.PlayerId, cp.To, cp.Text);     // client → client relay
                        }
                    }
                    break;
                }

                case MessageType.RequestSave:
                {
                    // A client hit Save / Save-and-Exit in their pause menu.  Run a
                    // coordinated save — HostSaveNow is thread-safe (it enqueues the
                    // IL2CPP-touching part onto the main thread) so it's fine here on
                    // the poll thread.
                    var rq = env.GetPayload<RequestSavePayload>();
                    string reason = string.IsNullOrEmpty(rq?.Reason) ? "client-request" : rq!.Reason;
                    // Honor the requester's chosen save name as the session name.
                    string sess = MPSaveCoordinator.SanitizeSession(rq?.SaveName ?? "");
                    if (!string.IsNullOrEmpty(sess)) MPSaveCoordinator.ActiveSessionName = sess;
                    Plugin.Logger.LogInfo($"[Server] RequestSave from peer {peer.Id} (reason={reason}, exiting={rq?.Exiting}, name='{rq?.SaveName}') — coordinated save.");
                    MPSaveCoordinator.HostSaveNow(reason);
                    break;
                }

                case MessageType.PhaseReport:
                {
                    var ph = env.GetPayload<PhaseReportPayload>();
                    if (ph != null && SenderIs(ph.PlayerId, senderPid, env.Type))
                        RecordPhaseReport(ph);   // dict write — safe here
                    break;
                }

                case MessageType.RegisterCashier:
                {
                    var rc = env.GetPayload<RegisterCashierPayload>();
                    if (rc == null || !SenderIs(rc.PlayerId, senderPid, env.Type)) break;
                    // Sweep batch 11: an empty Address is a LOST CONTEXT, not a permission
                    // failure — resolve the building from the station's stable id and stamp
                    // it before the check (and before Apply/Broadcast: receivers need the
                    // address for synthetic staffing).  The check below then judges the
                    // sender's actual rights instead of auto-failing on a blank.
                    bool rcStamped = false;
                    if (string.IsNullOrEmpty(rc.Address))
                    {
                        string resolved = MPRegisterSync.ResolveAddressForStation(rc.StationId);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            rc.Address = resolved; rcStamped = true;
                            Plugin.Logger.LogInfo($"[Server] RegisterCashier from '{senderPid}' carried no address — resolved '{resolved}' from station id (batch 11).");
                        }
                    }
                    // Round-42: owner OR granted helper — the owner-only anti-spoof gate predates helper
                    // grants and silently dropped a helper's register duty (field-proven: the guest worked
                    // the till, the host never learned, the simulator election never handed off, and the
                    // simulating owner's customers queued at an unmanned register).
                    bool dutyAllowed = SenderOwns(rc.Address, senderPid);
                    if (!dutyAllowed && !string.IsNullOrEmpty(rc.Address))
                    {
                        string dOwner = (BuildingOwners.TryGetValue(rc.Address, out var dg) && !string.IsNullOrEmpty(dg)) ? dg
                                      : (BuildingRealEstateOwners.TryGetValue(rc.Address, out var dr) ? dr : "");
                        string dOwnerPid = (dOwner == "host") ? MPConfig.PlayerId : dOwner;
                        dutyAllowed = !string.IsNullOrEmpty(dOwnerPid)
                                      && (GrantSync.IsGranted(GrantKind.Housing, dOwnerPid, senderPid)
                                       || GrantSync.IsGranted(GrantKind.Business, dOwnerPid, senderPid));
                    }
                    if (!dutyAllowed)
                    {
                        LogRejectThrottled("RegisterCashier", rc.Address, $"from '{senderPid}' — sender neither owns nor is granted");
                        break;
                    }
                    MPRegisterSync.Apply(rc);
                    // Batch 11: a stamped address must ride the relay too — receivers need it
                    // for synthetic staffing, and the original envelope still carries "".
                    Broadcast(rcStamped ? MessageEnvelope.Create(MessageType.RegisterCashier, rc.PlayerId, rc) : env);   // relay duty state to everyone (idempotent at sender)
                    break;
                }

                case MessageType.PlayerStaffRoster:
                {
                    var sr = env.GetPayload<PlayerStaffRosterPayload>();
                    if (sr == null)
                    {
                        // CROSSHR-ROSTER-1 (2026-09-16): on the 0916 game patch the host stopped injecting a
                        // partner's staff - its NetStats IN carried no PlayerStaffRoster at all - and this was the
                        // one exit that said nothing. A payload that fails to deserialise is a bug to diagnose from
                        // the log (user ruling 2026-09-16), so it is named here; the sender's copies stay absent.
                        Plugin.Logger.LogWarning($"[Server] PlayerStaffRoster from '{senderPid}': payload did not deserialise - dropped ({env.Data?.Length ?? 0} chars).");
                        break;
                    }
                    if (!SenderIs(sr.PlayerId, senderPid, env.Type)) break;
                    if (!SenderOwns(sr.AddressKey, senderPid))
                    {
                        Plugin.Logger.LogWarning($"[Server] PlayerStaffRoster for '{sr.AddressKey}' from '{senderPid}' — sender doesn't own/rent it — dropped.");
                        break;
                    }
                    if ((sr.Staff?.Count ?? 0) > 200) { Plugin.Logger.LogWarning($"[Server] PlayerStaffRoster '{sr.AddressKey}': implausible staff count — dropped."); break; }
                    MPRegisterSync.ApplyRoster(sr);
                    Broadcast(env);   // relay to everyone (idempotent at sender)
                    break;
                }

                case MessageType.RemoteSale:
                {
                    var rs = env.GetPayload<RemoteSalePayload>();
                    if (rs != null) HandleRemoteSale(rs, senderPid);
                    break;
                }

                case MessageType.StorageOp:     // v16 (storage unification): both containers, one channel
                {
                    var sq = env.GetPayload<StorageOpPayload>();
                    if (sq != null) HandleStorageOp(sq, senderPid);
                    break;
                }

                case MessageType.StorageRes:
                {
                    var sr = env.GetPayload<StorageResPayload>();
                    if (sr != null) HandleStorageRes(sr, senderPid);
                    break;
                }

                case MessageType.ServiceCarStop:     // 2026-09-02: rider asks the service car's OWNER to stop it
                case MessageType.ServiceCarResume:   // …or to resume it (boarded / cancelled)
                {
                    var sp = env.GetPayload<ServiceCarPayload>();
                    if (sp != null) ServiceCars.HostRoute(env.Type, sp, senderPid);
                    break;
                }

                case MessageType.TrunkDetailReq:   // v17: borrower wants a trunk's full cargo detail
                {
                    var tq = env.GetPayload<TrunkDetailReqPayload>();
                    if (tq != null) HandleTrunkDetailReq(tq, senderPid);
                    break;
                }

                case MessageType.TrunkDetailRes:
                {
                    var tr = env.GetPayload<TrunkDetailResPayload>();
                    if (tr != null) HandleTrunkDetailRes(tr, senderPid);
                    break;
                }

                case MessageType.HelperOrderForward:
                {
                    var ho = env.GetPayload<HelperOrderPayload>();
                    if (ho != null) HandleHelperOrder(ho, senderPid);
                    break;
                }

                case MessageType.CustomerPuppetState:   // a client simulator's stream — apply locally + relay
                {
                    var cp = env.GetPayload<CustomerPuppetStatePayload>();
                    if (cp != null && cp.SimulatorPid == senderPid)
                    {
                        GameStatePatcher.EnqueueOnMainThread(() => CustomerPuppets.ApplyState(cp));
                        BroadcastCustomerPuppets(cp, peer.Id);   // T8: inside-players only, no sender echo (SimulatorPid filter stays as the belt)
                    }
                    break;
                }

                case MessageType.RegisterServe:         // a client simulator's serve beat — apply + relay
                {
                    var rs = env.GetPayload<RegisterServePayload>();
                    if (rs != null && rs.SimulatorPid == senderPid)
                    {
                        GameStatePatcher.EnqueueOnMainThread(() => CustomerPuppets.ApplyServe(rs));
                        BroadcastRegisterServe(rs, peer.Id);   // T8
                    }
                    break;
                }

                case MessageType.CustomerPuppetEmote:   // a client simulator's customer emoji — apply + relay
                {
                    var ce = env.GetPayload<CustomerPuppetEmotePayload>();
                    if (ce != null && ce.SimulatorPid == senderPid)
                    {
                        GameStatePatcher.EnqueueOnMainThread(() => CustomerPuppets.ApplyEmote(ce));
                        BroadcastCustomerEmote(ce, peer.Id);   // T8
                    }
                    break;
                }

                case MessageType.CustomerPuppetLook:    // a client simulator's customer look — apply + relay
                {
                    var cl = env.GetPayload<CustomerPuppetLookPayload>();
                    if (cl != null && cl.SimulatorPid == senderPid)
                    {
                        GameStatePatcher.EnqueueOnMainThread(() => CustomerPuppets.ApplyLook(cl));
                        BroadcastCustomerLook(cl, peer.Id);   // T8 (also feeds the entry replay cache)
                    }
                    break;
                }

                case MessageType.BuildingInteriorDelta:   // v13 (Stage 1b): a permitted edit as its ops
                {
                    var bid = env.GetPayload<InteriorEditDeltaPayload>();
                    if (bid != null) HandleBuildingInteriorDelta(bid, senderPid);
                    break;
                }

                case MessageType.CashSync:
                {
                    var c = env.GetPayload<CashSyncPayload>();
                    if (c == null || !SenderIs(c.PlayerId, senderPid, env.Type)) break;
                    // A poisoned figure (NaN) would corrupt the manifest cash this
                    // player gets back on reconnect; negatives are legitimate
                    // (overdraft, like the bank).
                    if (!IsSaneMoney(c.Money, 10_000_000_000f))
                    {
                        Plugin.Logger.LogWarning($"[Server] CashSync from '{senderPid}': implausible money {c.Money} — dropped.");
                        break;
                    }
                    RecordCash(senderPid, c.Money);   // pure C# dict write — safe here
                    break;
                }

                default:
                    Plugin.Logger.LogWarning($"[Server] Unexpected message type {env.Type} from peer {peer.Id}");
                    break;
            }
        }

        // ── Message handlers ──────────────────────────────────────────────────

        /// <summary>Identity check: a payload field naming the SENDING player must
        /// match the connection's verified identity (bound at Hello).  Mismatches
        /// are invalid input — logged and dropped by the caller.  allowEmpty admits
        /// an unset field for payloads where the host derives the sender itself.</summary>
        private static bool SenderIs(string claimed, string verified, MessageType type, bool allowEmpty = false)
        {
            if (claimed == verified) return true;
            if (allowEmpty && string.IsNullOrEmpty(claimed)) return true;
            Plugin.Logger.LogWarning($"[Server] {type}: payload identity '{claimed}' does not match connection '{verified}' — dropped.");
            return false;
        }

        /// <summary>Finite and within a sanity cap — NaN/Infinity break every
        /// comparison and aggregate they touch, so no money figure from the
        /// network gets past this.</summary>
        internal static bool IsSaneMoney(float v, float cap = 1_000_000_000f)   // internal: also used by InteriorSync's owner-snapshot price gate
            => !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= cap;

        // ── Join control: bans (kick/reject — stand until the host re-hosts)
        //    and mid-game join requests awaiting the host's approval. ────────
        // CONCURRENT: written on the main thread (kick/reject UI), read on the
        // poll thread (HandleHello ban check).  ConcurrentDictionary used as a set.
        private static readonly ConcurrentDictionary<string, byte> _banned = new();
        private static readonly Dictionary<int, (MPLink peer, HelloPayload hello)> _pendingJoins = new();
        // JOIN-WAIT-1 (C3): when each parked request arrived, and how many heartbeat lines it has spent.
        private static readonly Dictionary<int, long> _pendingSince = new();
        private static readonly Dictionary<int, int>  _pendingHbLines = new();
        private static long _pendingHbNextMs;

        /// <summary>JOIN-WAIT-1 (C2): a parked joiner is not in _clients, so no broadcast reaches them and they sat on
        /// "Connected to host" with an empty player list and no timeout. Send THIS peer a lobby update carrying the
        /// awaiting-approval token; the real roster (empty token) replaces it the moment the host accepts.</summary>
        private static void SendJoinParkedNotice(MPLink peer)
        {
            try
            {
                var payload = new LobbyUpdatePayload
                {
                    Players = new List<string>(),   // they are NOT in the roster yet — saying otherwise would be a lie on their screen
                    EnforceStartingCash = EnforceStartingCash,
                    LoadMode = !string.IsNullOrEmpty(ChosenLoadSession),
                    LoadSessionName = ChosenLoadSession,
                    HostExpress = true,
                    JoinStatus = "awaiting-approval",
                };
                peer.Send(MessageEnvelope.Create(MessageType.LobbyUpdate, "host", payload));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] parked-join notice (JOIN-WAIT-1): {ex.Message}"); }
        }

        /// <summary>JOIN-WAIT-1 (C3): while any join request is parked, say so every 10 s — the host's approval popup
        /// only ticks while in game, and a missed popup used to leave the joiner (and the disconnect pause) hanging
        /// with nothing in the log. Capped at 30 lines per waiting request.</summary>
        private static void TickPendingJoinHeartbeat()
        {
            try
            {
                long now = TickMs64;
                if (now < _pendingHbNextMs) return;
                _pendingHbNextMs = now + 10000;
                int n;
                var parked = new List<(int peerId, string pid)>();
                lock (_pendingJoins)
                {
                    n = _pendingJoins.Count;
                    foreach (var kv in _pendingJoins) parked.Add((kv.Key, kv.Value.hello.PlayerId));
                }
                if (n == 0) { lock (_pendingSince) { _pendingSince.Clear(); _pendingHbLines.Clear(); } return; }
                // Review L1: every parked request on the line; the 30-line cap is PER request, so a newer
                // request keeps reporting after an older one has spent its lines.
                var parts = new List<string>();
                lock (_pendingSince)
                {
                    foreach (var (peerId, pid) in parked)
                    {
                        long since = now;
                        if (_pendingSince.TryGetValue(peerId, out var t)) since = t;
                        _pendingHbLines.TryGetValue(peerId, out int lines);
                        if (lines >= 30) continue;                 // capped: 30 lines per wait
                        _pendingHbLines[peerId] = lines + 1;
                        parts.Add($"'{pid}' {((now - since) / 1000)}s");
                    }
                }
                if (parts.Count == 0) return;
                Plugin.Logger.LogInfo($"[Join] {n} request(s) pending approval: {string.Join(", ", parts)}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Join] pending-join heartbeat (JOIN-WAIT-1): {ex.Message}"); }
        }

        /// <summary>Snapshot for the host's approval popup.</summary>
        public static List<(int peerId, string playerId)> PendingJoinList
        {
            get
            {
                var outp = new List<(int, string)>();
                lock (_pendingJoins)
                    foreach (var kv in _pendingJoins) outp.Add((kv.Key, kv.Value.hello.PlayerId));
                return outp;
            }
        }

        /// <summary>Host approved a mid-game joiner.</summary>
        public static void AcceptPendingJoin(int peerId)
        {
            (MPLink peer, HelloPayload hello) entry;
            lock (_pendingJoins)
            {
                if (!_pendingJoins.TryGetValue(peerId, out entry)) return;
                _pendingJoins.Remove(peerId);
            }
            lock (_pendingSince) { _pendingSince.Remove(peerId); _pendingHbLines.Remove(peerId); }   // JOIN-WAIT-1: this wait is over
            if (!entry.peer.IsAlive)
            { Plugin.Logger.LogInfo($"[Server] join request from '{entry.hello.PlayerId}' expired (disconnected)."); return; }
            Plugin.Logger.LogInfo($"[Server] host ACCEPTED mid-game join: '{entry.hello.PlayerId}'.");
            RegisterAndProcessJoin(entry.peer, entry.hello);
        }

        /// <summary>Host rejected a mid-game joiner — banned until re-host.</summary>
        public static void RejectPendingJoin(int peerId)
        {
            (MPLink peer, HelloPayload hello) entry;
            lock (_pendingJoins)
            {
                if (!_pendingJoins.TryGetValue(peerId, out entry)) return;
                _pendingJoins.Remove(peerId);
            }
            lock (_pendingSince) { _pendingSince.Remove(peerId); _pendingHbLines.Remove(peerId); }   // JOIN-WAIT-1: this wait is over
            Ban(entry.hello);
            try { entry.peer.Disconnect(System.Text.Encoding.UTF8.GetBytes("BAMP:rejected")); } catch { }
            Plugin.Logger.LogInfo($"[Server] host REJECTED mid-game join: '{entry.hello.PlayerId}' (banned until re-host).");
        }

        /// <summary>Host kicked a lobby player — banned until re-host.</summary>
        public static void KickFromLobby(string playerId)
        {
            if (string.IsNullOrEmpty(playerId) || playerId == MPConfig.PlayerId) return;
            _banned[playerId] = 0;
            if (StableIdByPlayer.TryGetValue(playerId, out var st) && !string.IsNullOrEmpty(st)) _banned[st] = 0;
            foreach (var kv in _peerNames)
                if (kv.Value == playerId)
                {
                    foreach (var p in _clients.Keys)
                        if (p.Id == kv.Key) { try { p.Disconnect(System.Text.Encoding.UTF8.GetBytes("BAMP:kicked")); } catch { } break; }
                    break;
                }
            LobbyRemove(playerId);
            BroadcastLobbyUpdate();
            Plugin.Logger.LogInfo($"[Server] KICKED '{playerId}' from the lobby (banned until re-host).");
        }

        private static void Ban(HelloPayload hello)
        {
            _banned[hello.PlayerId] = 0;
            if (!string.IsNullOrEmpty(hello.StableId)) _banned[hello.StableId] = 0;
        }

        /// <summary>Clear join control on a fresh hosting session ("re-form
        /// the lobby" = bans lift, pending requests drop).</summary>
        public static void ResetJoinControl()
        {
            _banned.Clear();
            lock (_pendingJoins) _pendingJoins.Clear();
            lock (_pendingSince) { _pendingSince.Clear(); _pendingHbLines.Clear(); }   // JOIN-WAIT-1
        }

        /// <summary>The Hello binds this connection's identity for the whole
        /// session — refuse empty ids, ids duplicating the host's own, identity
        /// switches on a live connection, and ids already bound to another live
        /// connection.  Everything downstream (money, saves, ownership) keys on
        /// this binding.</summary>
        private static bool ValidateHelloIdentity(MPLink peer, HelloPayload hello)
        {
            string refuse = "";
            if (string.IsNullOrWhiteSpace(hello.PlayerId))
                refuse = "empty PlayerId";
            else if (hello.PlayerId == MPConfig.PlayerId
                || (!string.IsNullOrEmpty(hello.StableId) && hello.StableId == MPConfig.StableId))
                refuse = "claims the HOST's identity";
            else if (_peerNames.TryGetValue(peer.Id, out var bound) && bound != hello.PlayerId)
            {
                // Keep the original binding; don't disconnect a live player.
                Plugin.Logger.LogWarning($"[Server] peer {peer.Id} ('{bound}') re-Hello'd as '{hello.PlayerId}' — identity switch refused.");
                return false;
            }
            else
            {
                // RECONNECT-TAKEOVER (2026-06-19): the same identity bound to ANOTHER peer is a STALE ghost
                // connection — the player dropped and is rejoining before LiteNetLib timed the old peer out.
                // The old behavior (refuse the NEW peer) left them unable to rejoin until that timeout, which
                // silently disabled ALL our MP suppression on the client (the vanilla-skip-after-reconnect bug).
                // Instead: kick the stale peer and accept this one. Pre-remove the stale binding FIRST so its
                // async OnPeerDisconnected runs only peer-level cleanup, NOT the player-level
                // LobbyRemove/RemovePlayer that would clobber the reconnecting player — their StableId-keyed
                // state (cash/ownership) persists and the existing reconnect path re-sends live world state.
                int staleId = -1;
                foreach (var kv in _peerNames)
                {
                    if (kv.Key == peer.Id) continue;
                    if (kv.Value == hello.PlayerId
                        || (!string.IsNullOrEmpty(hello.StableId)
                            && StableIdByPlayer.TryGetValue(kv.Value, out var st) && st == hello.StableId))
                    {
                        staleId = kv.Key;
                        break;
                    }
                }
                if (staleId >= 0)
                {
                    _peerNames.TryRemove(staleId, out _);   // remove BEFORE disconnect → its cleanup skips player-level removal
                    try
                    {
                        foreach (var p in _clients.Keys)
                            if (p.Id == staleId) { p.Disconnect(System.Text.Encoding.UTF8.GetBytes("BAMP:takeover")); break; }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] takeover disconnect of stale peer {staleId}: {ex.Message}"); }
                    Plugin.Logger.LogWarning($"[Server] '{hello.PlayerId}' reconnecting — dropped stale peer {staleId}, accepting new peer {peer.Id} (takeover).");
                }
            }
            if (refuse == "") return true;
            Plugin.Logger.LogWarning($"[Server] Hello from '{hello.PlayerId}' (peer {peer.Id}): {refuse} — disconnected.");
            try { peer.Disconnect(System.Text.Encoding.UTF8.GetBytes("BAMP:identity")); } catch { }
            return false;
        }

        /// <summary>Refuse a peer running an incompatible build BEFORE binding any
        /// identity — a protocol-number mismatch means the wire format differs (an
        /// out-of-date mod build would misparse messages), and a game-version
        /// mismatch means the two installs would desync.  The refusal tag carries
        /// the host's versions so the client can show exactly what to match.</summary>
        private static bool ValidateHelloVersion(MPLink peer, HelloPayload hello)
        {
            string hostGame = MPSaveManager.GameVersionNameCached();
            string hostBuild = MPContentFingerprint.GameBuildId;
            // Game-version check is skipped when either side is unknown (empty) so a
            // not-yet-cached host can't wrongly reject; the protocol number always gates.
            bool protocolOk = hello.Protocol == ProtocolInfo.Version;
            bool gameOk = string.IsNullOrEmpty(hello.Game) || string.IsNullOrEmpty(hostGame) || hello.Game == hostGame;
            // 2026-09-01 (update-impact review, user-approved): `Game` is the save-folder name ("1.0") and the
            // content fingerprint hashes item/business NAMES — the 2026-09-01 Steam update moved neither while
            // changing the save schema. The game build id moves on every game compile; a mismatch is refused so
            // an updated host and a not-yet-updated joiner cannot trade a save neither writes the same way.
            bool buildOk = string.IsNullOrEmpty(hello.GameBuild) || string.IsNullOrEmpty(hostBuild) || hello.GameBuild == hostBuild;
            if (protocolOk && gameOk && !buildOk)
            {
                Plugin.Logger.LogWarning(
                    $"[Server] Hello from '{hello.PlayerId}' refused — game BUILD mismatch " +
                    $"(joiner build {hello.GameBuild} vs host {hostBuild}; both report game '{hostGame}', mod {hello.Version}/p{hello.Protocol}).");
                try { peer.Disconnect(System.Text.Encoding.UTF8.GetBytes($"BAMP:build:{hostBuild}")); } catch { }
                // Round-215 lesson: tell the HOST too, or they only see "friend never appeared".
                try { MPCanvasUI.PostLobbyNotice($"{hello.PlayerId} can't join — game build mismatch. Update Big Ambitions on both machines, then rejoin."); } catch { }
                return false;
            }
            if (protocolOk && gameOk)
            {
                // Round-102: `Game` is only the version-FOLDER name, so two installs a month
                // apart both pass it while carrying different item data. Compare the finer
                // content fingerprint and record a mismatch in the host log — evidence for a
                // future bug report, never a refusal.
                MPContentFingerprint.HostCompare(hello.PlayerId, hello.Content);
                // Round-253 (user-directed 2026-08-13): diff the joiner's installed-mod list
                // against ours and INFORM both sides on a mismatch — players were blind to
                // install deltas (divergent prices/content read as mod bugs, and a player's
                // own missing/extra mod silently changes their game). Never a gate.
                try
                {
                    string mine = MPContentFingerprint.CachedMods, theirsMods = hello.Mods ?? "";
                    if (string.IsNullOrEmpty(theirsMods))
                        Plugin.Logger.LogInfo($"[Content] '{hello.PlayerId}' sent no mod list (older build) — mod diff skipped.");
                    else if (!string.IsNullOrEmpty(mine)
                             && MPContentFingerprint.DiffMods(mine, theirsMods, out var onlyMine, out var onlyTheirs, out int nMine, out int nTheirs))
                    {
                        ModMismatchByPlayer[hello.PlayerId] = (nTheirs, nMine);   // round-253b: feeds the lobby start-gate popup
                        string detail = $"only on host ({nMine}): {(nMine > 0 ? onlyMine : "-")} | only on '{hello.PlayerId}' ({nTheirs}): {(nTheirs > 0 ? onlyTheirs : "-")}";
                        Plugin.Logger.LogWarning($"[Content] MOD MISMATCH: '{hello.PlayerId}' runs a different mod set — {detail}. "
                            + "Different game content can change prices, items and behavior between machines; informational only, join proceeds.");
                        // (No posted lobby notice — round-253f: the host's strip line is DERIVED
                        // live from ModMismatchByPlayer ∩ LobbyPlayers, no timer to manage.)
                        // Round-253b (user test: the in-world host saw NOTHING — the lobby
                        // strip is invisible in-world): a mid-game host gets a toast instead.
                        try
                        {
                            GameStatePatcher.EnqueueOnMainThread(() =>
                            {
                                try
                                {
                                    if (!IsInLobby)
                                        PassengerHud.Toast($"{hello.PlayerId} is joining with DIFFERENT mods ({nTheirs} extra / {nMine} missing vs yours) — game content may differ.", 20f);
                                }
                                catch { }
                            });
                        }
                        catch { }
                        try
                        {
                            Send(peer, MessageEnvelope.Create(MessageType.ModMismatch, "host", new ModMismatchPayload
                            {
                                Summary = $"Your mods differ from the host's ({nTheirs} extra / {nMine} missing) — game content may differ.",
                                Detail  = detail
                            }));
                        }
                        catch { }
                    }
                    else
                    {
                        ModMismatchByPlayer.TryRemove(hello.PlayerId, out _);   // round-253b: a rejoin with aligned mods clears the record
                    }
                }
                catch { }
                return true;
            }

            Plugin.Logger.LogWarning(
                $"[Server] Hello from '{hello.PlayerId}' refused — version mismatch " +
                $"(client mod {hello.Version}/p{hello.Protocol}/{hello.Game} vs host {MyPluginInfo.PLUGIN_VERSION}/p{ProtocolInfo.Version}/{hostGame}).");
            try { peer.Disconnect(System.Text.Encoding.UTF8.GetBytes($"BAMP:version:{MyPluginInfo.PLUGIN_VERSION}|{hostGame}")); } catch { }
            // Round-215: tell the HOST too — they only saw "friend never appeared"
            // and filed "impossible to join" (field report 2026-07-31, host 0.1.14
            // vs joiner 0.1.16). Name whichever side is the mismatch we detected.
            try
            {
                string detail = !protocolOk
                    ? $"they have mod {hello.Version}, you have {MyPluginInfo.PLUGIN_VERSION}"
                    : $"they have game {hello.Game}, you have {hostGame}";
                MPCanvasUI.PostLobbyNotice($"{hello.PlayerId} can't join — {detail}. Both need the same version.");
            }
            catch { }
            return false;
        }

        /// <summary>Round-253b: per-player mod-mismatch records (extra = mods only they have,
        /// missing = mods only the host has), written at Hello, cleared when a rejoin arrives
        /// with aligned lists. Read by the lobby start-gate popup.</summary>
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int extra, int missing)> ModMismatchByPlayer = new();

        /// <summary>Multi-line summary of mismatched CURRENTLY-PRESENT players ("" = none).</summary>
        internal static string ModMismatchSummary()
        {
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in ModMismatchByPlayer)
                    if (LobbyPlayers.Contains(kv.Key))
                        parts.Add($"• {kv.Key}: {kv.Value.extra} mod(s) you don't have, missing {kv.Value.missing} of yours");
                return string.Join("\n", parts);
            }
            catch { return ""; }
        }

        /// <summary>Round-253f: compact one-liner for the lobby strip — a LIVE READ (events
        /// over timers, user ruling): present exactly while a mismatched player is.</summary>
        internal static string ModMismatchStripLine()
        {
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in ModMismatchByPlayer)
                    if (LobbyPlayers.Contains(kv.Key))
                        parts.Add($"{kv.Key} ({kv.Value.extra} extra / {kv.Value.missing} missing)");
                return parts.Count == 0 ? "" : "Mods differ vs " + string.Join(", ", parts);
            }
            catch { return ""; }
        }

        private static void HandleHello(MPLink peer, MessageEnvelope env)
        {
            var hello = env.GetPayload<HelloPayload>();
            if (hello == null) return;
            if (!ValidateHelloVersion(peer, hello)) return;
            if (!ValidateHelloIdentity(peer, hello)) return;

            // Banned (kicked or rejected) — out until the host re-hosts.
            if (_banned.ContainsKey(hello.PlayerId) || (!string.IsNullOrEmpty(hello.StableId) && _banned.ContainsKey(hello.StableId)))
            {
                Plugin.Logger.LogInfo($"[Server] Hello from BANNED '{hello.PlayerId}' — disconnected.");
                try { peer.Disconnect(System.Text.Encoding.UTF8.GetBytes("BAMP:banned")); } catch { }
                return;
            }

            Plugin.Logger.LogInfo($"[Server] Hello from '{hello.PlayerId}' (v{hello.Version})");

            // Same-protocol version SKEW passes the hard gate (by design) but is
            // exactly where the silent-loss class lives in the field (2026-07-20
            // severity review) — make it visible to the host instead of silent.
            if (!string.IsNullOrEmpty(hello.Version) && hello.Version != MyPluginInfo.PLUGIN_VERSION)
            {
                Plugin.Logger.LogWarning($"[Server] VERSION SKEW: '{hello.PlayerId}' runs v{hello.Version}, host runs v{MyPluginInfo.PLUGIN_VERSION} — they can connect, but fixes only protect updated machines (Steam large-transfer fixes need both sides).");
                GameStatePatcher.EnqueueOnMainThread(() => PassengerHud.Toast($"{hello.PlayerId} runs v{hello.Version} (you: v{MyPluginInfo.PLUGIN_VERSION}) — recommend updating everyone.", 6f));
            }

            // Mid-game joins need the HOST'S APPROVAL — park the request; the
            // in-game popup accepts or rejects it.
            if (!IsInLobby)
            {
                // JOIN-WAIT-1 (C1, user ruling): a RETURNING player skips the popup. "Returning" = this
                // hosting session already bound their stable id (StableIdByPlayer is written at Hello and is
                // NOT cleared on disconnect — only by ResetJoinControl/re-host), so they were approved once
                // already or started in the lobby. Bans are impossible here: the ban gate above disconnects
                // a kicked/rejected id before this point. Genuinely NEW peers still park for approval.
                bool returning = false;
                if (!string.IsNullOrEmpty(hello.StableId))
                    foreach (var kv in StableIdByPlayer)
                        if (kv.Value == hello.StableId && kv.Key != MPConfig.PlayerId) { returning = true; break; }
                if (returning)
                {
                    Plugin.Logger.LogInfo($"[Server] returning player '{hello.PlayerId}' auto-accepted (JOIN-WAIT-1)");
                    RegisterAndProcessJoin(peer, hello);   // exactly what the host's accept click calls
                    return;
                }
                lock (_pendingJoins) _pendingJoins[peer.Id] = (peer, hello);
                lock (_pendingSince) _pendingSince[peer.Id] = TickMs64;   // JOIN-WAIT-1 (C3): for the host's pending-join heartbeat
                Plugin.Logger.LogInfo($"[Server] mid-game join request from '{hello.PlayerId}' — awaiting host approval.");
                SendJoinParkedNotice(peer);   // JOIN-WAIT-1 (C2): tell them they are waiting, instead of a silent empty lobby
                return;
            }

            _clients[peer] = 0;
            _peerNames[peer.Id] = hello.PlayerId;
            _peerBuild[peer.Id] = (hello.Version ?? "", hello.CargoDelta, hello.ExpressLane);   // round-281/283: bound with the name, dropped with the peer
            if (!string.IsNullOrEmpty(hello.StableId))
            {
                StableIdByPlayer[hello.PlayerId] = hello.StableId;
                PlayerColours.Learn(hello.PlayerId, PlayerColours.HostAssign(hello.StableId));   // 2026-09-05 colours: permanent slot, assigned at first connection
                // Round-224: a CashSync that arrived BEFORE this mapping filed under the
                // display name — re-key it now or it stays invisible to every stable-id
                // lookup forever (the on-change gate never re-sends an unchanged wallet).
                if (CashByStableId.TryRemove(hello.PlayerId, out var earlyCash))
                {
                    CashByStableId[hello.StableId] = earlyCash;
                    Plugin.Logger.LogInfo($"[Server] cash re-keyed from early-join alias '{hello.PlayerId}' → stable id.");
                }
                GrantSync.NoteName(hello.StableId, hello.PlayerId);   // remember the name for owners' grantee lists
            }

            // Add to lobby and tell everyone
            LobbyAdd(hello.PlayerId);
            try { JoinedAtByPid[hello.PlayerId] = Environment.TickCount; } catch { }   // round-184: resurrection window anchor
            BroadcastLobbyUpdate();
            Plugin.Logger.LogInfo($"[Server] '{hello.PlayerId}' joined lobby. Players: {string.Join(", ", LobbyPlayers)}");
        }

        /// <summary>Approved mid-game join: register the peer and run the
        /// load chain (host-stored save → fresh character).</summary>
        private static void RegisterAndProcessJoin(MPLink peer, HelloPayload hello)
        {
            // Re-check at approval time: another connection may have taken this
            // identity while the request sat in the queue.
            if (!ValidateHelloIdentity(peer, hello)) return;
            {
                _clients[peer] = 0;
                _peerNames[peer.Id] = hello.PlayerId;
                _peerBuild[peer.Id] = (hello.Version ?? "", hello.CargoDelta, hello.ExpressLane);   // round-281/283 (mid-game join leg)
                if (!string.IsNullOrEmpty(hello.StableId))
                {
                    StableIdByPlayer[hello.PlayerId] = hello.StableId;
                    PlayerColours.Learn(hello.PlayerId, PlayerColours.HostAssign(hello.StableId));   // 2026-09-05 colours: permanent slot, assigned at first connection
                // Round-224: a CashSync that arrived BEFORE this mapping filed under the
                // display name — re-key it now or it stays invisible to every stable-id
                // lookup forever (the on-change gate never re-sends an unchanged wallet).
                if (CashByStableId.TryRemove(hello.PlayerId, out var earlyCash))
                {
                    CashByStableId[hello.StableId] = earlyCash;
                    Plugin.Logger.LogInfo($"[Server] cash re-keyed from early-join alias '{hello.PlayerId}' → stable id.");
                }
                    GrantSync.NoteName(hello.StableId, hello.PlayerId);
                }
                // Late join — keep the roster current for everyone (the in-game F9
                // window reads LobbyPlayers) by adding + re-broadcasting it.
                LobbyAdd(hello.PlayerId);
                BroadcastLobbyUpdate();

                // Mid-session JOIN/RECONNECT into an active MP save session (Phase
                // 4d core): if we hold a stored .hsg for this stableId, send it as
                // LoadData so they load in under their character.  The lobby flow
                // only covers peers present at StartLoadGame — anyone arriving
                // after (e.g. the host re-hosted a save and clicked Start before
                // the client finished re-joining) lands HERE; without this they
                // sat in the lobby forever.  World snapshots are NOT sent now:
                // they flow via MarkPlayerInGame once their scene loads (pre-
                // release: the frozen-until-synced path; post-release: the
                // reconnect branch).  Everything below is pure C# (version path
                // pre-cached) — safe on this poll thread.
                bool sentLoad = false;
                try
                {
                    string session = MPSaveCoordinator.ActiveSessionName;
                    string stable  = hello.StableId ?? "";
                    if (!string.IsNullOrEmpty(session) && !string.IsNullOrEmpty(stable))
                    {
                        // Phase 3: if the joiner offers a pending disconnect save for THIS session, request it
                        // first — we validate its ACTUAL in-game day on upload (HandleClientDisconnectUpload)
                        // before deciding, then send the real load. Only the disconnect file is eligible.
                        bool offerMatches = hello.HasDisconnectSave &&
                            string.Equals(hello.DisconnectSessionBase, MPSaveCoordinator.StripAutoSuffix(session), StringComparison.Ordinal);
                        // Handoff slice 3: names can collide across DIFFERENT worlds (esp. after a
                        // handoff/fork) — when both sides know their world identity, they must agree.
                        // Either side empty (pre-field marker / unstamped manifest) = legacy name-only.
                        if (offerMatches && !string.IsNullOrEmpty(hello.DisconnectPlaythroughId))
                        {
                            // The ACTIVE world's identity, not a re-read of the base manifest
                            // (review fix 2026-07-23: the base folder can lack a manifest —
                            // host loaded an '-auto' variant — or hold a same-named DIFFERENT
                            // world; both misjudged the offer).
                            string hostPid = MPSaveCoordinator.ActivePlaythroughId;
                            if (string.IsNullOrEmpty(hostPid))
                                try { hostPid = MPSaveManager.ReadManifest(MPSaveCoordinator.StripAutoSuffix(session))?.PlaythroughId ?? ""; } catch { }
                            if (!string.IsNullOrEmpty(hostPid) && hostPid != hello.DisconnectPlaythroughId)
                            {
                                offerMatches = false;
                                Plugin.Logger.LogWarning($"[Server] '{hello.PlayerId}' offered a disconnect save named for this session but from a DIFFERENT world (lineage mismatch) — ignoring the offer; sending their stored save.");
                            }
                        }
                        if (offerMatches)
                        {
                            Send(peer, MessageEnvelope.Create(MessageType.LoadData, "host", new LoadDataPayload
                            { SessionName = MPSaveCoordinator.StripAutoSuffix(session), AwaitClientDisconnectUpload = true }));
                            sentLoad = true;
                            Plugin.Logger.LogInfo($"[Server] Mid-session join by '{hello.PlayerId}': disconnect save offered (claimed day={hello.DisconnectDay}) — requesting upload for validation.");
                        }
                        else
                        {
                            sentLoad = SendMidJoinLoadData(peer, hello.PlayerId, stable, session);
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Mid-session LoadData: {ex.Message}"); }

                // Running game but NO session yet (host never saved): the joiner
                // would otherwise sit in the lobby forever — fresh-character
                // instruction (rejoiners keep nothing in this case anyway).
                if (!sentLoad && string.IsNullOrEmpty(MPSaveCoordinator.ActiveSessionName))
                {
                    try
                    {
                        Send(peer, MessageEnvelope.Create(MessageType.LoadData, "host", new LoadDataPayload
                        {
                            SessionName = "",
                            HsgGzipBase64 = "",
                            Money = Math.Max(0f, GetKnownCash(hello.PlayerId)),
                            FallbackSettings = LastStartSettings,
                            LoadGen = MintLoadGen(hello.PlayerId),   // round-284 load ticket
                        }));
                        sentLoad = true;
                        Plugin.Logger.LogInfo($"[Server] Mid-game join by '{hello.PlayerId}' (no session yet) — sent fresh-character instruction.");
                    }
                    catch (Exception ex2) { Plugin.Logger.LogWarning($"[Server] Mid-game fresh-join: {ex2.Message}"); }
                }
                if (sentLoad) return;

                // Late join — game already in progress, send world snapshot immediately
                if (!BroadcastWorldSnapshotEnabled)
                {
                    Plugin.Logger.LogWarning($"[Server] Late join by '{hello.PlayerId}' — WorldSnapshot SKIPPED (kill-switch active).");
                }
                else
                {
                    // Late join: building these snapshots iterates IL2CPP objects
                    // (gi.BuildingRegistrations, gi.rivalStates, GetRivalData …),
                    // which is unsafe from this background poll thread — marshal
                    // onto the main thread.  (Wave-13 class bug; only fires on a
                    // mid-game join so it had gone unnoticed.)
                    var joinPeer = peer;
                    var joinName = hello.PlayerId;
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        try
                        {
                            var snapshot = BuildWorldSnapshot();
                            Send(joinPeer, MessageEnvelope.Create(MessageType.Welcome, "host", snapshot));
                            Plugin.Logger.LogInfo($"[Server] Late join by '{joinName}', sent world snapshot.");
                            SendJoinReplayTo(joinPeer);   // rivals, businesses, register duty, passengers, market, shop prices
                            // Parked cars near the joiner — resync as soon as their position is known.
                            ParkedVehicleSync.ForgetPeer(joinName);
                            TrafficSync.ForgetPeer(joinName);   // review B1: same rule for the traffic identity map
                            ClearPlayerApplying(joinName);      // v9: fresh load — they drop applies until Settled/Running
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Late-join snapshot: {ex.Message}"); }
                    });
                }
            }
        }

        // ── Startup pause hold ────────────────────────────────────────────────

        private static void HandleClientPlayerInGame(string senderPid, MessageEnvelope env)
        {
            var payload = env.GetPayload<PlayerInGamePayload>();
            if (payload != null && !SenderIs(payload.PlayerId, senderPid, MessageType.PlayerInGame, allowEmpty: true)) return;
            MarkPlayerInGame(senderPid);
        }

        /// <summary>The on-connect JOIN REPLAY — every host-authoritative state a joiner needs that is
        /// otherwise only sent as an incremental/change broadcast (so a fresh joiner would miss it).
        /// SINGLE SOURCE OF TRUTH: both on-connect paths (late-join below and the startup-hold
        /// SendWorldStateTo) call this, so a NEW authoritative state is added in exactly ONE place
        /// (guards anti-pattern Class 4 — "new host-authoritative state not replayed to joiners").
        /// The caller sends the Welcome/WorldSnapshot first; path-specific extras (host profile,
        /// parked-car reset) stay at the call site.</summary>
        private static void SendJoinReplayTo(MPLink peer)
        {
            // Rival roster BEFORE businesses so building-owner ID strings resolve to names.
            SendRivalsSnapshotTo(peer);
            SendBusinessSnapshotTo(peer);    // business table (then deltas as they change)
            SendRegisterDutyTo(peer);        // who's currently on the registers (event-tracked)
            SendPassengerSnapshotTo(peer);   // passenger locks + who's riding (event-tracked)
            // Access grants: refresh the runtime table for the new roster + tell everyone, then hand the
            // joiner their OWN grantee list (incl. offline grantees) for the Permissions UI.
            RefreshGrantsAndBroadcast();
            if (_peerNames.TryGetValue(peer.Id, out var grantJoinerPid) && StableIdByPlayer.TryGetValue(grantJoinerPid, out var grantJoinerStable))
                SendOwnGrantsTo(peer, grantJoinerStable);
            // Merger phase 4a (review r2 M1): the joiner's COMPANY BOOKS ride the join replay that
            // actually runs. They used to sit in SendPermissionSnapshotTo, which had no call site at all -
            // so a mid-day joiner saw blank partner rows until the next day change, and a pay-all held
            // while it was offline was never delivered. THIS method is the one both the fresh join
            // (SendWorldStateTo) and the reconnect resync run, and it already knows the joiner's pid.
            if (!string.IsNullOrEmpty(grantJoinerPid)) { SendCompanyBooksTo(peer, grantJoinerPid); SendCompanyFeedTo(peer, grantJoinerPid); SendCompanyListsTo(peer, grantJoinerPid); SendCompanyCandidatesTo(peer, grantJoinerPid); }
            else Plugin.Logger.LogInfo($"[Books] join replay refused for peer {peer.Id}: that peer has no player id yet (its books arrive with the next publish).");
            SendMarketEventsTo(peer);        // active market events (change-broadcast only)
            SendPlayerShopPricesTo(peer);    // player-run shop prices (change-broadcast only)
            SendAppearanceSyncTo(peer);      // every player's appearance (else a joiner sees default avatars — broadcast-on-change only)
            foreach (var d in MPStockSync.AllDigests())   // un-entered shops' stocked-shelf sets, so the joiner's market floor is right from the start
                Send(peer, MessageEnvelope.Create(MessageType.ShopStockDigest, "host", d));
            // Shared pause is STATE, not an event — a joiner during a pause must be told, else it
            // runs at 1x while everyone else is frozen (covers fresh join + clean-drop reconnect).
            // Round-284: read the host's INTENT (_deliberatePause / _pausedByDisconnect, both
            // flipped synchronously at every site) — NOT TimeSync.ManualPaused.  This runs from a
            // main-thread queue job, and the unpause path (ResumeFromDisconnectPause) broadcasts
            // immediately but only ENQUEUES SetManualPause — so the applied flag can still read a
            // stale true here and re-pause the very rejoiner the unpause was for (log-proven,
            // 2026-08-19 soak).  Send-only-when-paused is kept: this is the join flow's ONLY
            // per-join pause inform, and any earlier broadcast a connected joiner received is
            // always paired with its own follow-up broadcast when that state ends.
            // 284c: express toward a capable peer, like every other host→client pause send —
            // ONE lane per peer keeps in-lane FIFO as the pause's total order (an ordered inform
            // stuck behind this replay's snapshot could be overtaken by a later express unpause,
            // and the older "paused" would land last).
            if (_deliberatePause || _pausedByDisconnect)
            {
                var pauseBytes = MessageEnvelope.Create(MessageType.ManualPause, "host", new ManualPausePayload { Paused = true }).Serialize();
                if (IsExpressCapablePeer(peer.Id)) peer.SendExpress(pauseBytes);
                else                               peer.Send(pauseBytes, reliable: true);
            }
        }

        /// <summary>Send a peer the full world state (owners+market, host profile,
        /// rivals, businesses) — on the main thread (IL2CPP).  Used for both late-join
        /// and the frozen-until-synced startup hold (sent while the client is frozen on
        /// the wait screen, so it applies before anyone gets control).</summary>
        public static void SendWorldStateTo(MPLink peer)
        {
            if (peer == null) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    var snapshot = BuildWorldSnapshot();
                    Send(peer, MessageEnvelope.Create(MessageType.Welcome, "host", snapshot));
                    BroadcastHostProfile();
                    SendJoinReplayTo(peer);
                    MPLoadProfiler.Mark($"HOST sent full world state to peer {peer.Id}");
                    Plugin.Logger.LogInfo($"[Server] Sent full world state to peer {peer.Id}.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendWorldStateTo: {ex.Message}"); }
            });
        }

        /// <summary>Re-send current register-duty state to a (re)joining peer.  Duty
        /// is event-tracked (broadcast only on change), and the peer's world reload
        /// on (re)connect runs MPRegisterSync.Reset(), clearing its map — while the
        /// resync above re-sends world/rivals/business but NOT duty.  Result before
        /// this: a reconnected client saw staffed registers as unstaffed ("no
        /// employees") until someone toggled duty off/on (field bug 2026-06-13).
        /// Sent peer-targeted, on the main thread (IL2CPP), after the world state so
        /// it lands on a peer that has already applied its reload + Reset.</summary>
        public static void SendRegisterDutyTo(MPLink peer)
        {
            if (peer == null) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    var duties = MPRegisterSync.SnapshotDuty();
                    foreach (var d in duties)
                        Send(peer, MessageEnvelope.Create(MessageType.RegisterCashier, d.PlayerId, d));
                    if (duties.Count > 0)
                        Plugin.Logger.LogInfo($"[Server] Re-sent {duties.Count} register-duty post(s) to peer {peer.Id}.");
                    // WS3: replay known staff rosters too (and nudge a fresh publish sweep for the host's own).
                    var rosters = MPRegisterSync.SnapshotRosters();
                    foreach (var r in rosters)
                        Send(peer, MessageEnvelope.Create(MessageType.PlayerStaffRoster, r.PlayerId, r));
                    if (rosters.Count > 0)
                        Plugin.Logger.LogInfo($"[Server] Re-sent {rosters.Count} staff roster(s) to peer {peer.Id}.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendRegisterDutyTo: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Called when a player (host or client) confirms their game SCENE has loaded.
        /// Frozen-until-synced: we do NOT release here — instead we send that peer its
        /// world snapshots (during the freeze).  The host's own scene-load also marks
        /// the host world-ready (its world is the source) and flushes snapshots to any
        /// clients already waiting.  Release happens later, in MarkWorldReady.
        /// </summary>
        public static void MarkPlayerInGame(string playerId)
        {
            // Round-276b (verifier finding 1): the latch clear is the FIRST act,
            // unconditional — the authenticated scene-load IS the signal.  The first
            // cut nested it behind sendTo.Count>0 (a peer lookup that can race empty)
            // and behind the second _startupReleased check (which can flip between
            // the two lock takes) — either race left the latch set, and with the
            // 'Loading' fallback gone nothing else would ever re-arm that member's
            // baseline.  Clearing is idempotent, so hoisting costs nothing.
            lock (_joinBaselineDone) _joinBaselineDone.Remove(playerId);
            // Round-284: the same authenticated signal is the gen path's MARK — set it
            // unconditionally (both the startup and the reconnect branch below run past
            // here), then fire any PARKED baseline (an express Settled/Running that beat
            // this message) whose ticket is still the expected one.  A parked gen
            // superseded by a newer serve is dropped — its load no longer exists.
            _markedInGame[playerId] = 1;
            if (_parkedBaselineGen.TryGetValue(playerId, out var parkedGen))
            {
                if (_expectedLoadGen.TryGetValue(playerId, out var expectedGen) && parkedGen == expectedGen)
                    FireJoinBaselineForGen(playerId, parkedGen, "PlayerInGame (parked express report)");
                else
                {
                    _parkedBaselineGen.TryRemove(playerId, out _);
                    Plugin.Logger.LogInfo($"[Server] parked join baseline for '{playerId}' (gen {parkedGen}) superseded by a newer serve — dropped.");
                }
            }
            bool hostJustReady = false;
            bool released;
            List<MPLink> sendTo = new();
            lock (_startupLock)
            {
                released = _startupReleased;
                if (released)
                {
                    // Mid-session reconnect (Phase 4d): the session is already live,
                    // so the frozen-until-synced flow won't run for this player —
                    // serve their world state directly now their scene is loaded.
                    if (playerId != MPConfig.PlayerId)
                        foreach (var p in _clients.Keys)
                            if (_peerNames.TryGetValue(p.Id, out var nm) && nm == playerId)
                                sendTo.Add(p);
                }
            }
            if (released)
            {
                foreach (var p in sendTo) SendWorldStateTo(p);
                if (sendTo.Count > 0)
                {
                    Plugin.Logger.LogInfo($"[Server] Reconnect: '{playerId}' scene loaded — sent live world state.");
                    // (round-276b: the join-baseline latch was already cleared at the
                    // top of this method — the scene-load signal, unconditional.)
                    MPChat.AddNotice($"{DisplayNameFor(playerId)} reconnected.");
                    BroadcastChat("", $"— {DisplayNameFor(playerId)} reconnected.");
                    ParkedVehicleSync.ForgetPeer(playerId);   // resync their parked cars after the reload
                    TrafficSync.ForgetPeer(playerId);         // review B1: same rule for the traffic identity map
                    ClearPlayerApplying(playerId);            // v9: mid-session reload — applies drop again until Settled/Running
                    // Lift the pause we applied when they dropped (only the
                    // disconnect pause — a deliberate manual pause stays).
                    ResumeFromDisconnectPause();
                }
                return;
            }

            lock (_startupLock)
            {
                if (_startupReleased) return;
                _inGamePlayers.Add(playerId);
                Plugin.Logger.LogInfo($"[Server] Scene loaded: '{playerId}' ({_inGamePlayers.Count}/{LobbyPlayers.Count})");
                MPLoadProfiler.Mark($"HOST scene-loaded '{playerId}' ({_inGamePlayers.Count}/{LobbyPlayers.Count})");

                bool isHost = playerId == MPConfig.PlayerId;
                if (isHost && !_hostSnapshotsReady)
                {
                    // The host's own world loaded — it's the source of truth, so it can
                    // serve snapshots now.  Flush to any clients already on the wait screen.
                    _hostSnapshotsReady = true;
                    hostJustReady = true;
                    foreach (var p in _clients.Keys)
                        if (_peerNames.TryGetValue(p.Id, out var nm) && _inGamePlayers.Contains(nm))
                            sendTo.Add(p);
                }
                else if (!isHost && _hostSnapshotsReady)
                {
                    // A client's scene is ready and the host can serve — send its world now.
                    foreach (var p in _clients.Keys)
                        if (_peerNames.TryGetValue(p.Id, out var nm) && nm == playerId)
                            sendTo.Add(p);
                }
            }

            foreach (var p in sendTo) SendWorldStateTo(p);
            // The host does NOT mark itself world-ready here.  Its game scene has
            // loaded (PlayerController spawned) so it can SERVE snapshots, but the
            // host's loading OVERLAY is usually still up and the world is still
            // streaming in.  MPCanvasUI.TickOverlayFreezeGate marks the host
            // world-ready only once that overlay has cleared — so the freeze holds
            // until the host (typically the last to finish loading) has truly
            // entered the game.
            if (hostJustReady)
                MPLoadProfiler.Mark("HOST world loaded — awaiting loading overlay before world-ready");
            BroadcastStartupStatus(WorldWaitingList());
        }

        /// <summary>A player has APPLIED the world sync.  Once everyone is world-ready
        /// the startup hold releases for all — so the game unfreezes onto a fully-synced
        /// world.  hostSelf marks the host (whose world is authoritative).</summary>
        // ── Load fence: phase visibility + excusals (stage-4 migration #2) ───
        // A client who bails to the MENU while still CONNECTED never fires a
        // disconnect — the old fence waited the full 90s timeout on them
        // (user backlog, 2026-06-11).  Clients report lifecycle transitions;
        // anyone parked in Menu >8s mid-fence is EXCUSED from the wait.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string phase, long atMs)> _peerPhase = new();
        // Round-274/M3: pids whose join-baseline save already fired this load — the
        // 'Settled' + 'Running' dual trigger must not double-save; cleared per client
        // by their next 'Loading' report (sent on every load path).
        private static readonly HashSet<string> _joinBaselineDone = new();
        // ── Round-284 "load tickets" — the gen-keyed join-baseline trigger that freed
        // phase reports for the express lane.  The host mints a ticket every time it
        // SERVES a player a load (every StartGameNew/StartGameLoad/LoadData that leads
        // to a load), carries it in the served payload, and the client echoes it in
        // every phase report.  A Settled/Running report fires the baseline IFF its gen
        // matches the expected ticket, that gen hasn't fired, and PlayerInGame has
        // landed (_markedInGame); a matching report that BEATS the ordered PlayerInGame
        // is PARKED and fired by MarkPlayerInGame — so an express report can neither
        // fire early nor be lost.  Gen 0 (older client) keeps the round-276 latch path
        // unchanged.  The counter is ONE process-lifetime int, never reset: a reissued
        // value could match a stale echo from an earlier session on the same link.
        private static int _loadGenCounter;
        private static readonly ConcurrentDictionary<string, int>  _expectedLoadGen   = new();   // pid → ticket of the load we last served them
        private static readonly ConcurrentDictionary<string, int>  _firedLoadGen      = new();   // pid → ticket whose baseline already fired
        private static readonly ConcurrentDictionary<string, int>  _parkedBaselineGen = new();   // pid → matching ticket awaiting its PlayerInGame
        private static readonly ConcurrentDictionary<string, byte> _markedInGame      = new();   // pid → MarkPlayerInGame landed for the served load

        /// <summary>Round-284: mint a load ticket for ONE player at the moment a load is
        /// served to them.  Clears their mark/parked state — the serve supersedes the
        /// previous load's scene signal and any parked fire.</summary>
        private static int MintLoadGen(string pid)
        {
            int gen = System.Threading.Interlocked.Increment(ref _loadGenCounter);
            RecordExpectedLoadGen(pid, gen);
            return gen;
        }

        private static void RecordExpectedLoadGen(string pid, int gen)
        {
            if (string.IsNullOrEmpty(pid)) return;
            _expectedLoadGen[pid] = gen;
            _markedInGame.TryRemove(pid, out _);
            _parkedBaselineGen.TryRemove(pid, out _);
            // 284b (verifier F-3, known residual): a PlayerInGame for the PREVIOUS load already
            // on the wire can land after this clear and re-set the mark — a one-RTT window in
            // which the next load's Settled fires against the old scene's mark.  Reachable only
            // by serving a new load to a client that is IN-WORLD, and today that path ejects
            // the client outright (round-240 known defect) before any such Settled — so the
            // window is closed in practice.  If an in-game host switch-save feature ever ships,
            // carry the gen ON PlayerInGame and match it here instead of trusting arrival time.
        }

        /// <summary>Round-284: one ticket for a load served by BROADCAST (the legacy
        /// StartGameLoad path) — every currently named peer expects the same gen.</summary>
        private static int MintSharedLoadGen()
        {
            int gen = System.Threading.Interlocked.Increment(ref _loadGenCounter);
            foreach (var pr in _clients.Keys)
                if (_peerNames.TryGetValue(pr.Id, out var nm)) RecordExpectedLoadGen(nm, gen);
            return gen;
        }

        /// <summary>Round-284: the gen-keyed join-baseline fire.  Idempotent per
        /// (player, gen); on failure the trigger is NOT consumed (fired stays unset, a
        /// parked entry stays), so the sibling Settled/Running report or the next
        /// PlayerInGame retries — a deferred action must not eat its trigger.</summary>
        private static void FireJoinBaselineForGen(string pid, int gen, string via)
        {
            if (_firedLoadGen.TryGetValue(pid, out var fired) && fired == gen) return;
            try
            {
                // ARM before REQUEST (round-274d) — identical to the legacy latch fire, so
                // TickJoinBaselineVerify arms the same way on the direct and the parked path.
                MPSaveCoordinator.ArmJoinBaselineVerify(StableOfPid(pid));
                MPSaveCoordinator.RequestJoinBaseline();
                _firedLoadGen[pid] = gen;
                _parkedBaselineGen.TryRemove(pid, out _);
                Plugin.Logger.LogInfo($"[Server] join baseline fired for '{pid}' (load gen {gen}, via {via}).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Server] join baseline fire for '{pid}' (load gen {gen}, via {via}) failed: {ex.Message} — trigger kept for a retry.");
            }
        }
        // Round-276b: last non-empty Detail per player — lets a detailed re-report of an
        // unchanged phase still log (the probe's payload must survive losing the race).
        private static readonly ConcurrentDictionary<string, string> _peerPhaseDetail = new();
        /// <summary>Round-283: highest PhaseReport.Seq accepted from each player, and the running
        /// count of reports dropped as stale.  Cleared when that player leaves (below) and with the
        /// session — a client that restarts its process restarts its counter at 1, so a last-seen
        /// that outlived the connection would silently swallow every report of the next one.</summary>
        private static readonly ConcurrentDictionary<string, long> _peerPhaseSeq = new();
        private static long _phaseStaleDrops;
        private static long _phaseStaleLogNextTicks;
        private static readonly HashSet<string> _fenceExcused = new();
        private static long _fenceArmedAtMs;   // grace: no excusals in the first 15s

        /// <summary>HOST: validate a cross-player sale and credit the owner.
        /// AI-rival "owners" fall out here (not in the lobby roster).</summary>
        public static void HandleRemoteSale(RemoteSalePayload rs, string senderPid)
        {
            try
            {
                if (string.IsNullOrEmpty(rs.OwnerId) || string.IsNullOrEmpty(rs.BuyerId)) return;
                if (!SenderIs(rs.BuyerId, senderPid, MessageType.RemoteSale)) return;   // the buyer reports its OWN purchase
                if (!LobbyPlayers.Contains(rs.OwnerId))
                {
                    Plugin.Logger.LogInfo($"[RemoteSale] owner '{rs.OwnerId}' not a lobby player (AI rival?) — ignored.");
                    return;
                }
                if (rs.OwnerId == rs.BuyerId) return;
                if (rs.Total <= 0f || rs.Total > 100000f || float.IsNaN(rs.Total))
                {
                    Plugin.Logger.LogWarning($"[RemoteSale] rejected: implausible total ${rs.Total:F2} from '{rs.BuyerId}'.");
                    return;
                }
                // Order lines drive the authoritative stock decrement — a
                // negative Amount would CREDIT shelf stock.
                if (rs.Items.Count > 200 || rs.Items.Exists(it => it.Amount <= 0 || it.Amount > 10000 || string.IsNullOrEmpty(it.ItemName)))
                {
                    Plugin.Logger.LogWarning($"[RemoteSale] rejected: implausible order lines ({rs.Items.Count} items) from '{rs.BuyerId}'.");
                    return;
                }
                GameStatePatcher.EnqueueOnMainThread(() =>
                {
                    MPHub.DeliverSaleRevenue(rs.OwnerId, rs.BuyerId, rs.Total, rs.Address, rs.Desc);
                    // Slice 2: the sale consumes REAL stock.  The host is the
                    // interior authority — decrement here; the interior diff
                    // (hash covers cargo Amount) carries it to every machine.
                    string shortfall = GameStatePatcher.ApplySaleStockDecrement(rs.Address, rs.Items, rs.BuyerId);
                    // Oversell window (replica lag / racing buyers): the charge
                    // already happened buyer-side — surface the divergence to
                    // both parties instead of letting it pass silently.
                    if (!string.IsNullOrEmpty(shortfall))
                    {
                        MPHub.NotifyParty(rs.OwnerId, $"Stock shortfall at {rs.Address}: sold {shortfall} beyond shelf stock.");
                        MPHub.NotifyParty(rs.BuyerId, $"Heads up: {rs.Address} was short on {shortfall} — the shelf was out of sync.");
                    }
                });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[RemoteSale] {ex.Message}"); }
        }

        /// <summary>Broker for ALL shared storage since v16 (storage unification Stage B): an
        /// accessor wants an op on a container it does not own. The HOST resolves the owner itself
        /// (ruling 38 — the retired vehicle wire trusted a sender-supplied OwnerId): buildings
        /// from the host's own ledgers (clients keep no building→owner map), vehicles from
        /// PassengerSync.OwnerOf (populated by every fleet broadcast). Building ops are grant-gated
        /// here AND re-verified on the owner (the vehicle lock/grant gate stays owner-side —
        /// container-inherent, unchanged). Owner==host → apply on the main thread and answer the
        /// accessor directly; else forward to the owner's machine.</summary>
        public static void HandleStorageOp(StorageOpPayload req, string senderPid)
        {
            try
            {
                if (req == null) return;
                if (!SenderIs(req.PlayerId, senderPid, MessageType.StorageOp)) return;   // accessor reports its OWN action
                string ownerPid;
                if (req.Container == "vehicle")
                {
                    if (string.IsNullOrEmpty(req.VehicleId)) return;
                    ownerPid = PassengerSync.OwnerOf(req.VehicleId);
                    if (string.IsNullOrEmpty(ownerPid))
                    {
                        Plugin.Logger.LogWarning($"[Store] storage op on vehicle '{req.VehicleId}' dropped — the host has no owner on record for it (fleet not yet noted?).");
                        return;
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(req.AddressKey)) return;
                    string owner = (BuildingOwners.TryGetValue(req.AddressKey, out var o) && !string.IsNullOrEmpty(o)) ? o
                                 : (BuildingRealEstateOwners.TryGetValue(req.AddressKey, out var r) ? r : "");
                    if (string.IsNullOrEmpty(owner)) return;
                    ownerPid = (owner == "host") ? MPConfig.PlayerId : owner;   // resolve the host sentinel
                    // Grant gate — Housing OR Business, matching the authoritative check in OwnerApply:
                    // the Housing-only pre-filter silently dropped every cargo op from a business-only
                    // helper (round-38e relay/apply drift).
                    if (ownerPid != req.PlayerId
                        && !GrantSync.IsGranted(GrantKind.Housing, ownerPid, req.PlayerId)
                        && !GrantSync.IsGranted(GrantKind.Business, ownerPid, req.PlayerId)) return;
                }
                if (ownerPid == MPConfig.PlayerId)
                {
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        var res = StorageSync.OwnerApply(req);
                        if (res.PlayerId == MPConfig.PlayerId) StorageSync.OnResult(res);   // host is also the accessor
                        else SendHubTo(res.PlayerId, MessageType.StorageRes, res);
                    });
                }
                else
                {
                    SendHubTo(ownerPid, MessageType.StorageOp, req);   // forward to the owner's machine to apply
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Store] HandleStorageOp: {ex.Message}"); }
        }

        /// <summary>v17 trunk detail broker — the same routing shape as HandleStorageOp's vehicle
        /// branch (ruling 38: the host resolves the owner itself; no sender-supplied owner).</summary>
        public static void HandleTrunkDetailReq(TrunkDetailReqPayload req, string senderPid)
        {
            try
            {
                if (req == null || string.IsNullOrEmpty(req.VehicleId)) return;
                if (!SenderIs(req.PlayerId, senderPid, MessageType.TrunkDetailReq)) return;
                string ownerPid = PassengerSync.OwnerOf(req.VehicleId);
                if (string.IsNullOrEmpty(ownerPid))
                {
                    Plugin.Logger.LogWarning($"[Store] trunk detail on '{req.VehicleId}' dropped — the host has no owner on record for it.");
                    return;
                }
                if (ownerPid == MPConfig.PlayerId)
                {
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        var res = StorageSync.BuildTrunkDetail(req);
                        if (res.PlayerId == MPConfig.PlayerId) StorageSync.OnTrunkDetail(res);   // host is also the accessor
                        else SendHubTo(res.PlayerId, MessageType.TrunkDetailRes, res);
                    });
                }
                else
                {
                    SendHubTo(ownerPid, MessageType.TrunkDetailReq, req);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Store] HandleTrunkDetailReq: {ex.Message}"); }
        }

        /// <summary>v17: a vehicle owner answered a trunk-detail request — relay to the requester.</summary>
        public static void HandleTrunkDetailRes(TrunkDetailResPayload res, string senderPid)
        {
            try
            {
                if (res == null || string.IsNullOrEmpty(res.PlayerId)) return;
                if (res.PlayerId == MPConfig.PlayerId)
                    GameStatePatcher.EnqueueOnMainThread(() => StorageSync.OnTrunkDetail(res));   // host is the accessor
                else
                    SendHubTo(res.PlayerId, MessageType.TrunkDetailRes, res);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Store] HandleTrunkDetailRes: {ex.Message}"); }
        }

        /// <summary>A container owner answered a storage op — relay the verdict to the accessor.
        /// (Co-op trust model: the result is relayed as-is; a pending-request table could be added
        /// later to reject forged grants — out of scope for friends-only play.) ONE method for both
        /// containers — risk R14: the relay leg must be structurally identical so exercising either
        /// container exercises it.</summary>
        public static void HandleStorageRes(StorageResPayload res, string senderPid)
        {
            try
            {
                if (res == null || string.IsNullOrEmpty(res.PlayerId)) return;
                if (res.PlayerId == MPConfig.PlayerId)
                    GameStatePatcher.EnqueueOnMainThread(() => StorageSync.OnResult(res));   // host is the accessor
                else
                    SendHubTo(res.PlayerId, MessageType.StorageRes, res);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Store] HandleStorageRes: {ex.Message}"); }
        }

        /// <summary>Round-39f: a helper's machine hosted an NPC sale — route the paid order to the
        /// building owner (or adopt directly when the host owns it). Same routing shape as HandleStorageOp's building branch.</summary>
        public static void HandleHelperOrder(HelperOrderPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                if (!SenderIs(p.PlayerId, senderPid, MessageType.HelperOrderForward)) return;
                string owner = (BuildingOwners.TryGetValue(p.AddressKey, out var o) && !string.IsNullOrEmpty(o)) ? o
                             : (BuildingRealEstateOwners.TryGetValue(p.AddressKey, out var r) ? r : "");
                if (string.IsNullOrEmpty(owner)) return;
                string ownerPid = (owner == "host") ? MPConfig.PlayerId : owner;
                if (ownerPid != p.PlayerId
                    && !GrantSync.IsGranted(GrantKind.Housing, ownerPid, p.PlayerId)
                    && !GrantSync.IsGranted(GrantKind.Business, ownerPid, p.PlayerId)) return;   // grant gate
                if (ownerPid == MPConfig.PlayerId)
                    GameStatePatcher.EnqueueOnMainThread(() => CustomerEntrySync.OwnerAdoptForwardedOrder(p));
                else
                    SendHubTo(ownerPid, MessageType.HelperOrderForward, p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] HandleHelperOrder: {ex.Message}"); }
        }

        // (HandleVehicleCargoRes / HandleBuildingCargoRes retired with their wire types in v16 —
        // HandleStorageRes above is the one relay for both containers.)

        // STAGE 3 (2026-08-25): HandleBuildingInteriorEdit — the host route for the guest
        // whole-replica edit (type 140) — is RETIRED with the type.  Its grant rule (sender is
        // the owner, or holds a Housing/Business grant from the owner) lives on verbatim in
        // HandleBuildingInteriorDelta below.

        /// <summary>STAGE 1b (design 2026-08): route a permitted edit DELTA. Authorization: the
        /// sender must be the building's owner, or hold a Housing OR Business grant from the owner
        /// (business helpers forward furniture edits too — round-32 helper-aware placement).
        /// Host-owned building: adopt locally + relay the delta to subscribers minus the sender +
        /// stamp the send trackers (RelayDeltaAfterHostAdopt). Remote-owned: forward to the owner to
        /// adopt, apply to the HOST's own replica too (the host player's view must not wait for an
        /// owner round-trip — the owner no longer re-pushes after adopting; Q1 retired that), graft
        /// the host's owner cache, and relay to subscribers minus the sender.
        /// TRAP 1 (design): every apply is enqueued UNKEYED — the "interior:"+addr key is
        /// newest-wins full-state coalescing, and a superseded delta is a LOST EDIT.</summary>
        public static void HandleBuildingInteriorDelta(InteriorEditDeltaPayload p, string senderPid)
        {
            try
            {
                // Stage 2 review BLOCKER-U: a delta is empty only when it has NO ops AND NO designs —
                // a paint-only designer close legitimately carries zero item ops, and the old
                // ops-only guard silently dropped it (permanently: the sender's baseline had
                // already advanced, so the paint was never re-emitted).
                if (p == null || string.IsNullOrEmpty(p.AddressKey)
                    || ((p.Ops?.Count ?? 0) == 0 && (p.Designs?.Count ?? 0) == 0)) return;
                p.Ops ??= new System.Collections.Generic.List<InteriorItemOp>();   // ops-less payloads pass the guard; downstream loops must not NRE
                p.Designs ??= new System.Collections.Generic.List<InteriorDesignInfo>();
                string owner = (BuildingOwners.TryGetValue(p.AddressKey, out var o) && !string.IsNullOrEmpty(o)) ? o
                             : (BuildingRealEstateOwners.TryGetValue(p.AddressKey, out var r) ? r : "");
                // Review MAJOR-N: these refusals must be LOUD — the sender's baseline already
                // advanced, so a silently dropped delta is permanent divergence with no recurrence.
                if (string.IsNullOrEmpty(owner))
                { Plugin.Logger.LogWarning($"[Housing] interior delta for '{p.AddressKey}' from '{senderPid}' DROPPED — no recorded owner; the sender's edit is not conveyed."); return; }
                string ownerPid = (owner == "host") ? MPConfig.PlayerId : owner;
                if (ownerPid != senderPid
                    && !GrantSync.IsGranted(GrantKind.Housing, ownerPid, senderPid)
                    && !GrantSync.IsGranted(GrantKind.Business, ownerPid, senderPid))
                { Plugin.Logger.LogWarning($"[Housing] interior delta for '{p.AddressKey}' from '{senderPid}' DROPPED — no grant from '{ownerPid}'; the sender's edit is not conveyed."); return; }
                if (ownerPid == MPConfig.PlayerId)
                {
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        GameStatePatcher.ApplyInteriorEditDelta(p);
                        InteriorSync.RelayDeltaAfterHostAdopt(p, senderPid);
                    });   // UNKEYED — see trap 1 above
                }
                else
                {
                    SendHubTo(ownerPid, MessageType.BuildingInteriorDelta, p);   // the owner adopts the same ops
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        if (senderPid != MPConfig.PlayerId)   // host-as-guest already made the edit natively; re-applying is idempotent but pointless
                            GameStatePatcher.ApplyInteriorEditDelta(p);
                        InteriorSync.GraftDeltaOntoOwnerCache(p, senderPid, ownerPid);
                    });   // UNKEYED — see trap 1 above
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] HandleBuildingInteriorDelta: {ex.Message}"); }
        }

        /// <summary>Round-112 — route a HELPER's cleaning report (MessageType.BuildingDirtEdit) to whoever owns
        /// the building.  Authorization mirrors HandleBuildingInteriorDelta exactly: the sender must be the owner
        /// or hold a Housing/Business grant from them.  Deliberately narrow — this carries only floor-cell
        /// dirtiness, so unlike an interior payload it cannot bring anything else with it.</summary>
        public static void HandleBuildingDirtEdit(DirtEditPayload payload, string senderPid)
        {
            try
            {
                if (payload == null || string.IsNullOrEmpty(payload.AddressKey) || payload.Spots.Count == 0) return;
                string owner = (BuildingOwners.TryGetValue(payload.AddressKey, out var o) && !string.IsNullOrEmpty(o)) ? o
                             : (BuildingRealEstateOwners.TryGetValue(payload.AddressKey, out var r) ? r : "");
                if (string.IsNullOrEmpty(owner)) return;
                string ownerPid = (owner == "host") ? MPConfig.PlayerId : owner;
                if (ownerPid != senderPid
                    && !GrantSync.IsGranted(GrantKind.Housing, ownerPid, senderPid)
                    && !GrantSync.IsGranted(GrantKind.Business, ownerPid, senderPid))
                {
                    Plugin.Logger.LogWarning($"[Cleaning] dirt edit for '{payload.AddressKey}' from '{senderPid}' — no grant from '{ownerPid}' — dropped.");
                    return;
                }
                if (ownerPid == MPConfig.PlayerId)
                    GameStatePatcher.EnqueueOnMainThread(() => HelperCleaning.Apply(payload));
                else
                    SendHubTo(ownerPid, MessageType.BuildingDirtEdit, payload);   // the owner's machine adopts it
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cleaning] HandleBuildingDirtEdit: {ex.Message}"); }
        }

        public static void RecordPhaseReport(PhaseReportPayload? p)
        {
            if (p == null || string.IsNullOrEmpty(p.PlayerId)) return;

            // ── Round-283 FRESHNESS GUARD ────────────────────────────────────
            // A phase report is a LATEST-WINS state report about the sender, and everything
            // below treats it that way: _peerPhase[pid] is overwritten outright, and the
            // fence's excuse rule (TickStartupFence) acts on whatever phase is there NOW —
            // "phase == Menu for 8s ⇒ excuse them from the load wait".  So an OLD report
            // landing after a NEW one is not a harmless duplicate: a stale 'Menu' arriving
            // after 'Running' re-stamps the player as menu-bailed with a FRESH timestamp and
            // excuses a client that is actually in the world.  That is exactly the round-276
            // flap class (a live client repeatedly demoted by state that no longer described
            // it, which is what fed the join-save storm), so it is guarded at the door rather
            // than hoped away.
            //
            // Today the single ordered FIFO makes reordering impossible and this guard never
            // fires; it is a precondition for the express lane, not a fix for a live bug, and
            // it is deliberately in place BEFORE any sender puts these on that lane.
            // Seq == 0 means an older client that does not stamp: always apply, which is
            // byte-for-byte the behaviour those peers get today.
            if (p.Seq > 0)
            {
                long lastSeen = _peerPhaseSeq.TryGetValue(p.PlayerId, out var ls) ? ls : 0;
                if (p.Seq <= lastSeen)
                {
                    long n = System.Threading.Interlocked.Increment(ref _phaseStaleDrops);
                    long now = DateTime.UtcNow.Ticks;
                    if (now >= _phaseStaleLogNextTicks)
                    {
                        _phaseStaleLogNextTicks = now + TimeSpan.TicksPerSecond * 60;
                        Plugin.Logger.LogInfo($"[Server] phase: dropped a STALE report from '{p.PlayerId}' "
                            + $"(seq {p.Seq} <= last {lastSeen}, phase '{p.Phase}') — {n} stale drop(s) this session.");
                    }
                    return;
                }
                _peerPhaseSeq[p.PlayerId] = p.Seq;
            }

            bool changed = !_peerPhase.TryGetValue(p.PlayerId, out var prevPh) || prevPh.phase != p.Phase;
            _peerPhase[p.PlayerId] = (p.Phase, TickMs64);
            // v9 (review B1/M4): the applying-edge. First Settled/Running report of this load
            // latches "this client applies streams now" AND fires the round-197 one-shot
            // resend — HERE, not at the WorldReady message: the 20 s deadline path sends
            // WorldReady while the client still quiesce-drops, which ate the entire burst
            // (rivals + business deltas + for-sale list + rosters) with no second re-cover.
            // Settled/Running provably follow the quiesce end AND the 3 s settle window.
            if ((p.Phase == "Settled" || p.Phase == "Running") && _peerApplying.TryAdd(p.PlayerId, 1))
            {
                string applyPid = p.PlayerId;
                GameStatePatcher.EnqueueOnMainThread(() => ResendJoinerState(applyPid));
                // MERGER PHASE 3-C (C1): the RETURN LEG's send point. This edge is the FIRST moment
                // provably AFTER that peer's own save has loaded and settled (the quiesce end and the
                // 3 s settle window both precede it), so the returned state OVERWRITES their load
                // instead of being overwritten by it - which is exactly what world-ready would risk.
                // Enqueued second, and the main-thread queue is FIFO, so it also lands after the join
                // re-send above. One-shot per load (the _peerApplying latch).
                GameStatePatcher.EnqueueOnMainThread(() => HostReturnIfMarked(applyPid, broadcast: true));
            }
            // Round-276b (verifier finding 5): a bare report winning the race must not
            // silence a later, detailed report of the SAME phase — the detail is the
            // probe's whole payload.  Re-log when the detail changes, phase change or not.
            if (!changed && !string.IsNullOrEmpty(p.Detail))
            {
                _peerPhaseDetail.TryGetValue(p.PlayerId, out var prevDetail);
                if (p.Detail != prevDetail) changed = true;
            }
            if (!string.IsNullOrEmpty(p.Detail)) _peerPhaseDetail[p.PlayerId] = p.Detail;
            if (changed)
            {
                // Round-276 probe: the sender-side reason + our outbound queue depth to
                // that peer, on the one log line a field bundle reliably carries — the
                // client-side discriminators were unrecoverable in 20260818-215459
                // because peer-log collection shares the congested link.
                long outQ = 0; try { outQ = PendingSendBytesTo(p.PlayerId); } catch { }
                Plugin.Logger.LogInfo($"[Server] phase: '{p.PlayerId}' → {p.Phase}"
                    + (string.IsNullOrEmpty(p.Detail) ? "" : $" ({p.Detail})")
                    + (outQ > 0 ? $" outQ={outQ / 1024}KB" : ""));
            }
            // Round-271 (Fix A): baseline capture the moment a joiner can actually CONTRIBUTE
            // to it — their settled-gate, reported explicitly (world-ready is too early: their
            // upload is skipped and the checkpoint is born without them; 2026-06-23 contract,
            // field 20260816-213224 hole).  Deferred internally while the HOST is unsettled.
            // Round-274/M3: the 'Settled' report is one-shot and the transport can drop it —
            // the lifecycle's own 'Running' report (independent sender, ~same moment) is the
            // retry.  One baseline per client per load.
            // Round-276 (field 20260818-215459): the latch is NO LONGER cleared by the
            // 'Loading' phase string — that string is a heuristic inference (clock+overlay
            // sampling), and a live client's 6s clock wobble re-armed the latch, so every
            // wobble fired a full join-baseline save (~6.6MB mirrored per fire; three in
            // 15s collapsed the links).  The latch now clears only on the AUTHENTICATED
            // per-load join signal — MarkPlayerInGame (sender-verified scene-load, both
            // the startup and the mid-session reconnect branch) — and on disconnect.
            // Phase reports still TIME the fire (Settled/Running = the joiner can
            // contribute); they can no longer ARM it.
            // Round-284: a report that echoes a load ticket (LoadGen != 0) takes the
            // gen-keyed path instead — the latch machinery below remains solely for
            // gen-0 (pre-284) clients.
            // 284b (verifier F-2): the gen path deliberately does NOT require `changed`.
            // _peerPhase is the one per-player map a disconnect leaves standing, so a
            // rejoiner whose stale entry already reads "Running" would report Running
            // with changed == false — and a changed-gated fire would neither fire nor
            // park, with nothing left to retry it.  The gen path is idempotent per
            // (player, gen), so an unchanged re-report is a free retry, not a hazard.
            if (p.LoadGen != 0 && (p.Phase == "Settled" || p.Phase == "Running"))
            {
                // ── Round-284 gen path ────────────────────────────────────────
                // The report names the exact load it describes, so the fire needs
                // no cross-type arrival order: match the ticket, then either fire
                // (PlayerInGame already landed) or PARK for MarkPlayerInGame (the
                // express report beat the ordered scene-load signal).  A mismatched
                // gen is a stale report from a previous load — ignored for baseline
                // purposes only; the phase bookkeeping above already applied.
                _expectedLoadGen.TryGetValue(p.PlayerId, out var expectedGen);
                if (p.LoadGen != expectedGen)
                {
                    if (changed)   // log the first sighting, not every re-report
                        Plugin.Logger.LogInfo($"[Server] phase: '{p.PlayerId}' {p.Phase} carries load gen {p.LoadGen}, expected {expectedGen} — stale report from a previous load; not a baseline trigger.");
                }
                else if (_markedInGame.ContainsKey(p.PlayerId))
                    FireJoinBaselineForGen(p.PlayerId, p.LoadGen, $"'{p.Phase}' report");
                else if (!(_firedLoadGen.TryGetValue(p.PlayerId, out var fg) && fg == p.LoadGen))
                {
                    _parkedBaselineGen[p.PlayerId] = p.LoadGen;
                    Plugin.Logger.LogInfo($"[Server] join baseline for '{p.PlayerId}' PARKED (gen {p.LoadGen}: '{p.Phase}' arrived before PlayerInGame) — fires when the mark lands.");
                }
                return;
            }
            if (changed && (p.Phase == "Settled" || p.Phase == "Running"))
            {
                bool first; lock (_joinBaselineDone) first = _joinBaselineDone.Add(p.PlayerId);
                if (first)
                {
                    // Round-274/F1: the latch is kept (no double-save on the normal race),
                    // but the coordinator VERIFIES the joiner landed in the baseline after
                    // the upload window and refires once if not — so a 'Running' win over
                    // a still-closed settled-gate can no longer produce a fileless slot.
                    try
                    {
                        // Round-274d (verifier PLAUSIBLE-A): ARM before REQUEST — the main-thread
                        // tick can consume the request and bump the join-save sequence between
                        // these two statements, which would arm the verify against the NEXT join
                        // save and silently skip this one.  The refire path already orders it so.
                        MPSaveCoordinator.ArmJoinBaselineVerify(StableOfPid(p.PlayerId));
                        MPSaveCoordinator.RequestJoinBaseline();
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Settled baseline save: {ex.Message}"); }
                }
            }
            // (round-276: no 'Loading' clear here — see the comment above.)
        }

        /// <summary>Round-276: helpers exposing the transport's per-peer send backlog —
        /// the congestion signal for the join-baseline verifier and the phase probe.
        /// Round-282: the figure now INCLUDES the paced lane's unreleased bytes (see
        /// MPLink.PendingSendBytes).  A queue-depth reading must be the whole truth —
        /// a metered mirror is still data owed to that peer, and hiding it would have
        /// made the verifier read "link clear" while megabytes waited.  Expect outQ to
        /// stay non-zero LONGER than before for the same payload: pacing trades queue
        /// duration for head-of-line latency, on purpose.</summary>
        internal static long PendingSendBytesTo(string pid)
        {
            foreach (var peer in _clients.Keys)
                if (_peerNames.TryGetValue(peer.Id, out var p) && p == pid) return peer.PendingSendBytes;
            return 0;
        }

        internal static long PendingSendBytesToStable(string stable)
        {
            if (string.IsNullOrEmpty(stable)) return 0;
            foreach (var kv in StableIdByPlayer)
                if (kv.Value == stable) return PendingSendBytesTo(kv.Key);
            return 0;
        }

        // ── Gate self-heal (field 2026-07-19) ─────────────────────────────────
        // A client that reported scene-loaded (PlayerInGame) but never became
        // world-ready is missing its world sync — the field case: the Steam
        // relay refused the 826-building business snapshot mid-burst and it was
        // silently lost, so the client could never send WorldReady and the
        // startup overlay waited forever.  The transport now retries refused
        // sends, and THIS heals any residual cause: while the hold is up, a
        // player in-game but not world-ready gets the business snapshot
        // re-sent after 15s, max 3 attempts, 15s apart.  A peer whose queue is
        // still draining is skipped without spending an attempt — field
        // 2026-09-06: the heal re-sent 918 KB to a slow client whose first copy
        // was still in flight.
        private static readonly Dictionary<string, (int attempts, long lastMs)> _gateHeal = new();

        private static void TickGateHeal()
        {
            List<string>? candidates = null;
            lock (_startupLock)
            {
                if (_startupReleased) return;
                long now = TickMs64;
                foreach (var pid in _inGamePlayers)
                {
                    if (pid == MPConfig.PlayerId) continue;                     // host syncs itself
                    if (_worldReadyPlayers.Contains(pid) || _fenceExcused.Contains(pid)) continue;
                    _gateHeal.TryGetValue(pid, out var st);
                    if (st.lastMs == 0) { _gateHeal[pid] = (0, now); continue; } // arm on first sight
                    if (st.attempts >= 3 || now - st.lastMs < 15000) continue;
                    (candidates ??= new List<string>()).Add(pid);
                }
            }
            if (candidates == null) return;
            List<string>? needs = null;
            foreach (var pid in candidates)
            {
                // Burst fix 2026-09-10 (r2, review #1): the backlog read takes the transport's own
                // locks and a native Steam status call, so it runs OUTSIDE _startupLock. Bytes still
                // owed to this peer mean the first copy is in flight, not lost — re-arm the 15 s
                // window WITHOUT spending an attempt. The figure is the peer's WHOLE backlog (paced
                // store-mirror lane included), so a busy peer can defer the heal for as long as it
                // stays busy; the startup-timeout release still broadcasts the table to everyone
                // (review #2, accepted).
                long queued = PendingSendBytesTo(pid);
                long stamp = TickMs64;
                lock (_startupLock)
                {
                    if (_startupReleased || _worldReadyPlayers.Contains(pid) || _fenceExcused.Contains(pid)) continue;   // became ready between the two locks — nothing to heal
                    _gateHeal.TryGetValue(pid, out var st);
                    if (queued > 0) _gateHeal[pid] = (st.attempts, stamp);
                    else { _gateHeal[pid] = (st.attempts + 1, stamp); (needs ??= new List<string>()).Add(pid); }
                }
                if (queued > 0)
                    Plugin.Logger.LogInfo($"[Server] gate-heal deferred for '{pid}': {queued / 1024} KB still queued to them — nothing to heal yet.");
            }
            if (needs == null) return;
            foreach (var pid in needs)
            {
                var link = LinkForPlayer(pid);
                if (link == null) continue;
                Plugin.Logger.LogWarning($"[Server] gate-heal: '{pid}' is in-game but not world-ready — re-sending business snapshot.");
                SendBusinessSnapshotTo(link);
            }
        }

        /// <summary>Push a fresh business snapshot to one player by id (audit
        /// self-heal, gate-heal).  False when the player has no live link.</summary>
        public static bool SendBusinessSnapshotToPlayer(string playerId)
        {
            var link = LinkForPlayer(playerId);
            if (link == null) return false;
            SendBusinessSnapshotTo(link);
            return true;
        }

        private static MPLink? LinkForPlayer(string playerId)
        {
            foreach (var kv in _peerNames)
            {
                if (kv.Value != playerId) continue;
                foreach (var peer in _clients.Keys)
                    if (peer.Id == kv.Key) return peer;
            }
            return null;
        }

        /// <summary>Host main thread, ~1 Hz while hosting: excuse fence-waited
        /// players who bailed to the menu; release if nobody real remains.</summary>
        public static void TickFencePrune()
        {
            if (!_running) return;
            TickGateHeal();
            TickPendingJoinHeartbeat();   // JOIN-WAIT-1 (C3): a parked join request must not wait in silence
            bool release = false;
            List<string>? waiting = null;
            lock (_startupLock)
            {
                if (_startupReleased) return;
                long now = TickMs64;
                // Grace: the gap between leaving the lobby and the overlay
                // rising legitimately reports "Menu" — the prune excused a
                // LOADING client (2026-06-11).  Clients also now declare a
                // "Loading" intent at load-instruction receipt; this grace is
                // the belt to that suspender.
                if (now - _fenceArmedAtMs < 15000) return;
                foreach (var kv in _peerPhase)
                {
                    if (_worldReadyPlayers.Contains(kv.Key) || _fenceExcused.Contains(kv.Key)) continue;
                    if (!LobbyPlayers.Contains(kv.Key)) continue;
                    if (kv.Value.phase == "Menu" && now - kv.Value.atMs > 8000)
                    {
                        _fenceExcused.Add(kv.Key);
                        Plugin.Logger.LogInfo($"[Server] fence: '{kv.Key}' bailed to the menu — excused from the wait.");
                    }
                }
                waiting = LobbyPlayers.Where(p => !_worldReadyPlayers.Contains(p) && !_fenceExcused.Contains(p)).ToList();
                if (waiting.Count == 0 && _worldReadyPlayers.Count > 0)
                {
                    _startupReleased = true;
                    release = true;
                }
            }
            if (release) ReleaseStartupHold("remaining players ready (menu-bailers excused)");
        }

        /// <summary>v9 (review B1): per-CONNECTION-per-load latch — this player can APPLY
        /// streams, not just receive them. Set by the 'Settled'/'Running' phase reports
        /// (round-271's own "the joiner can contribute" edge; 'Settled' is one-shot and the
        /// lifecycle's 'Running' report is its per-load retry — round-274/M3). Cleared on
        /// disconnect and on the authenticated per-load join/reload signals (the same sites
        /// that call ParkedVehicleSync.ForgetPeer). Deliberately NOT derived from
        /// _worldReadyPlayers: that set is sticky once the startup hold releases (a rejoiner
        /// stays "ready" while loading), and the WorldReady MESSAGE itself can precede the
        /// client's 3 s settle window — both edges mark cars delivered to a client that
        /// provably drops them.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _peerApplying = new();

        internal static bool IsPlayerApplying(string playerId)
            => !string.IsNullOrEmpty(playerId) && _peerApplying.ContainsKey(playerId);

        /// <summary>v9: the per-load reset for the applying latch — called wherever the code
        /// already treats the player as freshly (re)loading (ForgetPeer sites, disconnect).</summary>
        internal static void ClearPlayerApplying(string playerId)
        {
            if (!string.IsNullOrEmpty(playerId)) _peerApplying.TryRemove(playerId, out _);
        }

        public static void MarkWorldReady(string playerId, bool hostSelf = false)
        {
            bool release = false;
            lock (_startupLock)
            {
                if (_startupReleased)
                {
                    // Mid-session reconnect: the session-wide release already
                    // happened (one-shot), but THIS player just froze on their
                    // local startup hold and applied the world — ack them with a
                    // direct StartupRelease so they unfreeze.  Idempotent.
                    foreach (var p in _clients.Keys)
                        if (_peerNames.TryGetValue(p.Id, out var nm) && nm == playerId)
                        {
                            Send(p, MessageEnvelope.Create(
                                MessageType.StartupRelease, "host", new StartupReleasePayload()));
                            Plugin.Logger.LogInfo($"[Server] Reconnect: released startup hold for '{playerId}'.");
                        }
                    return;
                }
                if (string.IsNullOrEmpty(playerId)) return;
                if (hostSelf && !_hostSnapshotsReady) return;   // host world not loaded yet
                _worldReadyPlayers.Add(playerId);
                _fenceExcused.Remove(playerId);   // they made it after all
                int total = LobbyPlayers.Count(p => !_fenceExcused.Contains(p));
                int ready = LobbyPlayers.Count(p => _worldReadyPlayers.Contains(p) && !_fenceExcused.Contains(p));
                Plugin.Logger.LogInfo($"[Server] World-ready: '{playerId}' ({ready}/{total})");
                MPLoadProfiler.Mark($"HOST world-ready '{playerId}' ({ready}/{total})");
                if (total > 0 && ready >= total)
                {
                    _startupReleased = true;
                    release = true;
                }
            }
            if (release) ReleaseStartupHold("all players world-ready");
            else         BroadcastStartupStatus(WorldWaitingList());
        }

        private static void HandleWorldReady(string senderPid, MessageEnvelope env)
        {
            var payload = env.GetPayload<PlayerInGamePayload>();
            if (payload != null && !SenderIs(payload.PlayerId, senderPid, MessageType.WorldReady, allowEmpty: true)) return;
            MarkWorldReady(senderPid);
            // Round-274/F3: a member counts as "served since load" only once they have
            // provably LOADED what was served (world-ready = world sync applied).  The
            // earlier send-time mark let a client that died mid-load keep a stale
            // pre-rollback disconnect offer alive behind an already-granted mark.
            try { MPSaveCoordinator.MarkServedSinceLoad(StableOfPid(senderPid)); } catch { }
            // Round-271 (Fix A): the join baseline save used to fire HERE, at world-ready —
            // structurally before the joiner's own settled-gate, so the checkpoint it produced
            // could never contain its trigger's member (field 20260816-213224: every first-join
            // slot born fileless).  It now fires on the joiner's 'Settled' phase report — the
            // same predicate that gates their upload — see HandlePhaseReport.
            // A player whose world just loaded missed any earlier loan-state
            // broadcast (session-load ledger, joins mid-loan) — re-broadcast.
            GameStatePatcher.EnqueueOnMainThread(MPHub.BroadcastLoansIfAny);
            // Round-197's one-shot resend used to fire HERE ("WorldReady is the first moment
            // provably AFTER the drop window") — v9 review M4 showed that is false on the
            // 20 s deadline path, where WorldReady goes out while the client still
            // quiesce-drops and the whole burst is eaten. The resend now fires on the
            // Settled/Running applying-edge in RecordPhaseReport, which provably follows
            // both the quiesce end and the settle window.
        }

        /// <summary>Round-197: everything a joiner's load-window drop list may have
        /// discarded, re-sent to that one client at WorldReady. Idempotent applies.</summary>
        private static void ResendJoinerState(string pid)
        {
            try
            {
                var peer = PeerForPid(pid);
                if (peer == null) return;
                SendRivalsSnapshotTo(peer);
                var bsnap = BusinessSync.BuildFullSnapshot();
                // v9 (T6): the join already delivered the full table seconds ago, and joining
                // itself always touches a few entries — so the old whole-table sig almost never
                // matched and this resend shipped ~0.8 MB per join. Ship only what changed since
                // the baseline captured at the join send (SendBusinessDeltaTo handles the
                // vanished-business / no-baseline / over-cap fallbacks to the full table).
                string bizLine = SendBusinessDeltaTo(peer, bsnap, PerBusinessSigs(bsnap));
                // v9: a daily for-sale flip that landed in this joiner's load-window drop
                // list has no other re-cover until the NEXT in-game day — ship the live list.
                var fsList = BusinessSync.CurrentForSaleList();
                if (fsList != null)
                    peer.Send(MessageEnvelope.Create(MessageType.BuildingsForSale, "host",
                              new BuildingsForSalePayload { BuildingsForSale = fsList }));
                int rosters = 0;
                foreach (var r in MPRegisterSync.SnapshotRosters())   // stored client rosters; also nudges the host's own publishes
                {
                    SendToPlayer(pid, MessageEnvelope.Create(MessageType.PlayerStaffRoster, r.PlayerId, r));
                    rosters++;
                }
                int duty = MPRegisterSync.SendDutyStateTo(pid);
                MPTakeover.HostHealUnfurnishedShopsFor(pid);   // round-204d: claimed-but-unfurnished takeover shops
                Plugin.Logger.LogInfo($"[Server] World-ready re-send to '{pid}': rivals + {bizLine} + {rosters} stored roster(s) (own publishes nudged) + {duty} duty entr(ies).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] ResendJoinerState: {ex.Message}"); }
        }

        /// <summary>True once the host's own game world has loaded (PlayerController
        /// spawned) so it can serve snapshots — even if its overlay is still up.</summary>
        public static bool HostSnapshotsReady
        {
            get { lock (_startupLock) return _hostSnapshotsReady; }
        }

        /// <summary>True once the host has been marked world-ready (its loading
        /// overlay cleared).  Used by the overlay gate to avoid re-marking.</summary>
        public static bool HostIsWorldReady
        {
            get { lock (_startupLock) return _worldReadyPlayers.Contains(MPConfig.PlayerId); }
        }

        /// <summary>Roster players who are NOT yet world-ready (for the wait screen).</summary>
        private static List<string> WorldWaitingList()
        {
            lock (_startupLock)
                return LobbyPlayers.Where(p => !_worldReadyPlayers.Contains(p) && !_fenceExcused.Contains(p)).ToList();
        }

        /// <summary>Players not yet world-ready — for the host's own startup screen.</summary>
        public static List<string> GetStartupWaitingFor()
        {
            lock (_startupLock)
            {
                if (_startupReleased) return new List<string>();
                return LobbyPlayers.Where(p => !_worldReadyPlayers.Contains(p) && !_fenceExcused.Contains(p)).ToList();
            }
        }

        private static void BroadcastStartupStatus(List<string> waiting)
        {
            Broadcast(MessageEnvelope.Create(
                MessageType.StartupStatus, "host",
                new StartupStatusPayload { WaitingFor = waiting }));
        }

        /// <summary>
        /// Host-side safety net: force-releases the startup hold even if not every
        /// player has reported in-game (e.g. one is stuck loading).  Called by the
        /// startup-timeout watchdog so the game can never freeze permanently.
        /// </summary>
        public static void ForceReleaseStartupHold()
        {
            lock (_startupLock)
            {
                if (_startupReleased) return;
                _startupReleased = true;
            }
            ReleaseStartupHold("startup timeout");
        }

        private static void ReleaseStartupHold(string reason)
        {
            // Frozen-until-synced: the world snapshots (owners/market, host profile,
            // rivals, businesses) were already sent to each peer DURING the hold (see
            // SendWorldStateTo on scene-load).  By the time we get here everyone is
            // world-ready, so release just unfreezes onto a fully-synced world.
            Plugin.Logger.LogInfo($"[Server] Releasing startup pause hold ({reason}) — world already synced.");
            MPLoadProfiler.Mark($"HOST ReleaseStartupHold ({reason}) — unfreeze (world pre-synced)");
            Broadcast(MessageEnvelope.Create(
                MessageType.StartupRelease, "host", new StartupReleasePayload()));
            GameStatePatcher.EnqueueOnMainThread(() => TimeSync.EndStartupHold());

            // Safety net: if we got here via the timeout watchdog (a client never
            // acked world-ready), make sure everyone still receives the world state.
            if (reason == "startup timeout")
            {
                GameStatePatcher.EnqueueOnMainThread(() =>
                {
                    try { BroadcastHostProfile(); Broadcast(MessageEnvelope.Create(MessageType.RivalsSnapshot, "host", BuildRivalsSnapshot())); BroadcastBusinessSnapshot(); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Timeout-release world resend: {ex.Message}"); }
                });
            }
        }

        // ── Manual pause ──────────────────────────────────────────────────────

        /// <summary>Record + broadcast a DELIBERATE pause (the pause button), tracked
        /// separately from the disconnect pause so ResumeFromDisconnectPause can restore it.</summary>
        public static void SetDeliberatePause(bool paused)
        {
            _deliberatePause = paused;
            BroadcastManualPause(paused);
        }

        /// <summary>Broadcasts the shared manual-pause state to all clients.
        /// 284c (user-approved): rides the EXPRESS lane toward capable peers — a pause must not
        /// sit behind a store mirror (the F-1 flicker: a press outliving F2's 8s echo bound).
        /// ManualPause carries no Seq on purpose: EVERY host→client pause send (this broadcast,
        /// the relay, the join-replay inform) uses the same lane toward a given peer, so in-lane
        /// FIFO is its total order; the F2 heartbeat is the floor beneath a mixed pairing.</summary>
        public static void BroadcastManualPause(bool paused)
        {
            if (!_running) return;
            var bytes = MessageEnvelope.Create(
                MessageType.ManualPause, "host", new ManualPausePayload { Paused = paused }).Serialize();
            foreach (var peer in _clients.Keys)
            {
                // Per-peer isolation, same shape as the GameTimeSync loop.
                try
                {
                    if (IsExpressCapablePeer(peer.Id)) peer.SendExpress(bytes);
                    else                               peer.Send(bytes, reliable: true);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] ManualPause → {peer.Describe}: {ex.Message}"); }
            }
            Plugin.Logger.LogInfo($"[Server] ManualPause broadcast: {paused}");
        }

        private static void HandleClientManualPause(MPLink sender, string senderPid, MessageEnvelope env)
        {
            var payload = env.GetPayload<ManualPausePayload>();
            if (payload == null) return;

            // Apply on the host, then relay to every other client.  Track the deliberate
            // intent (NOT the disconnect pause) so a later reconnect restores it (M6).
            _deliberatePause = payload.Paused;
            TimeSync.SetManualPause(payload.Paused);
            // 284c: relay on the express lane toward capable peers — same reasoning and
            // same per-peer shape as BroadcastManualPause above.
            var relayBytes = MessageEnvelope.Create(MessageType.ManualPause, "host", payload).Serialize();
            foreach (var peer in _clients.Keys)
                if (peer.Id != sender.Id)
                {
                    try
                    {
                        if (IsExpressCapablePeer(peer.Id)) peer.SendExpress(relayBytes);
                        else                               peer.Send(relayBytes, reliable: true);
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] ManualPause relay → {peer.Describe}: {ex.Message}"); }
                }
            Plugin.Logger.LogInfo($"[Server] ManualPause from {senderPid}: {payload.Paused} — relayed.");
        }

        // ── Multiplayer game settings ─────────────────────────────────────────

        /// <summary>
        /// Returns a mod-defined difficulty preset.  The numbers come from the GAME's
        /// own difficulty assets (DifficultySetting.GetDifficultySettings — the same
        /// source the native new-game screen reads), so the MP presets match
        /// single player exactly (EA 0.11: Easy $15,000 / 0.7× rival attacks,
        /// Normal $10,000 / 1.0×, Hard $4,200 / 1.2×) and track future game
        /// rebalances automatically.  The multiplayer overrides stay on top (no
        /// tutorial, no energy need — the DTO defaults).  If the asset registry
        /// is not alive yet (this is also called from a UI field initializer),
        /// falls back to the EA 0.11 numbers; any lobby click re-pulls live.
        /// </summary>
        public static GameVariablesDto Preset(string difficulty)
        {
            var dto = new GameVariablesDto();   // carries the MP overrides (tutorial off by default, energy off)
            dto.Difficulty = (difficulty == "Easy" || difficulty == "Hard") ? difficulty : "Normal";
            try
            {
                // Difficulty enum resolved reflectively (same type-name independence
                // as BuildGameVariables' difficulty write).
                var m = typeof(DifficultySetting).GetMethod("GetDifficultySettings",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var enumType = m?.GetParameters()[0].ParameterType;
                if (m != null && enumType != null
                    && m.Invoke(null, new object[] { Enum.Parse(enumType, dto.Difficulty) }) is DifficultySetting ds)
                {
                    dto.StartingAge                     = ds.startingAge;
                    dto.StartingMoney                   = ds.startingMoney;
                    dto.TaxPercentage                   = ds.taxPercentage;
                    dto.DaysPerYear                     = ds.daysPerYear;
                    dto.MarketPriceMultiplier           = ds.marketPriceMultiplier;
                    dto.EmployeeHourlySalaryMultiplier  = ds.employeeHourlySalaryMultiplier;
                    dto.BankInterestMultiplier          = ds.bankInterestMultiplier;
                    dto.BankInterestRate                = 0f;   // 1.0 banking overhaul removed the flat rate (BankSettings assets x the multiplier now); wire field kept for shape
                    dto.RivalsDifficultyMultiplier      = ds.rivalsDifficultyMultiplier;
                    dto.BaseCustomerPromotionMultiplier = ds.baseCustomerPromotionMultiplier;
                    dto.WholesaleUrgentFeeMultiplier    = ds.wholesaleUrgentFeeMultiplier;
                    dto.ImporterUrgentFeeMultiplier     = ds.importerUrgentFeeMultiplier;
                    dto.ExportMultiplier                = ds.exportMultiplier;
                    dto.SellingMultiplier               = ds.sellingMultiplier;   // 1.0-new; LIVE under Custom
                    // NOT ds.tutorialEnabled — off by default; a host world with it on carries it to the
                    // session (D26, 2026-09-12), because the read-back below sends gv.tutorialEnabled on.
                    Plugin.Logger.LogInfo($"[Server] Difficulty preset '{dto.Difficulty}' from game asset: cash={dto.StartingMoney} rivals×{dto.RivalsDifficultyMultiplier:0.00}.");
                    return dto;
                }
                Plugin.Logger.LogWarning($"[Server] Preset '{difficulty}': native difficulty asset unavailable — EA 0.11 fallback numbers.");
            }
            // Unwrap TargetInvocationException (field 2026-07-18: a modded install logged
            // only the generic "Exception has been thrown by the target of an invocation"
            // — the INNER exception is the actual cause and must self-diagnose).
            catch (Exception ex)
            {
                var root = ex; while (root.InnerException != null) root = root.InnerException;
                Plugin.Logger.LogWarning($"[Server] Preset '{difficulty}': {root.GetType().Name}: {root.Message} — EA 0.11 fallback numbers.");
            }
            switch (dto.Difficulty)
            {
                case "Easy": dto.StartingMoney = 15000; dto.RivalsDifficultyMultiplier = 0.7f; break;
                case "Hard": dto.StartingMoney = 4200;  dto.RivalsDifficultyMultiplier = 1.2f; break;
                default:     dto.StartingMoney = 10000; dto.RivalsDifficultyMultiplier = 1f;   break;
            }
            return dto;
        }

        /// <summary>Deep-clones a settings DTO and overrides its starting cash + age,
        /// so each player can be sent the same world settings with a per-player
        /// starting balance + age without aliasing the shared lobby settings object.</summary>
        private static GameVariablesDto CloneWithCash(GameVariablesDto src, int cash, int age)
        {
            GameVariablesDto copy;
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(src);
                copy = Newtonsoft.Json.JsonConvert.DeserializeObject<GameVariablesDto>(json) ?? new GameVariablesDto();
            }
            catch { copy = new GameVariablesDto(); }
            copy.StartingMoney = cash;
            if (age > 0) copy.StartingAge = age;
            return copy;
        }

        /// <summary>Converts a settings DTO into the game's GameVariables struct.</summary>
        public static GameVariables BuildGameVariables(GameVariablesDto dto)
        {
            var gv = new GameVariables();
            try
            {
                gv.startingAge                       = dto.StartingAge;
                gv.disableAging                      = dto.DisableAging;
                // 0% drain = the exact native "off" (bar hidden, NoEat sad-period
                // excluded); the legacy bool still honored for old-peer DTOs.
                gv.disableEnergy                     = dto.DisableEnergy || dto.NeedsDrainPercent == 0;
                MPNeedsTuning.Apply(dto, "game settings");
                gv.disableHappiness                  = dto.DisableHappiness;
                gv.allCoursesUnlocked                = dto.AllCoursesUnlocked;
                gv.startingMoney                     = dto.StartingMoney;
                gv.taxPercentage                     = dto.TaxPercentage;
                gv.daysPerYear                       = dto.DaysPerYear;
                gv.marketPriceMultiplier             = dto.MarketPriceMultiplier;
                gv.employeeHourlySalaryMultiplier    = dto.EmployeeHourlySalaryMultiplier;
                gv.bankInterestMultiplier            = dto.BankInterestMultiplier;
                gv.tutorialEnabled                   = dto.TutorialEnabled;
                // gv.bankInterestRate — removed by the 1.0 banking overhaul; the multiplier above carries difficulty
                gv.rivalsDifficultyMultiplier        = dto.RivalsDifficultyMultiplier;
                gv.disableVehicleDamage              = dto.DisableVehicleDamage;
                gv.disableVehicleFuel                = dto.DisableVehicleFuel;
                gv.allContactsUnlocked               = dto.AllContactsUnlocked;
                gv.baseCustomerPromotionMultiplier   = dto.BaseCustomerPromotionMultiplier;
                gv.wholesaleUrgentFeeMultiplier      = dto.WholesaleUrgentFeeMultiplier;
                gv.importerUrgentFeeMultiplier       = dto.ImporterUrgentFeeMultiplier;
                gv.disableWholesaleAndImportLimits   = dto.DisableWholesaleAndImportLimits;
                gv.allProductsAvailableFromImporters = dto.AllProductsAvailableFromImporters;
                gv.exportMultiplier                  = dto.ExportMultiplier;
                gv.sellingMultiplier                 = dto.SellingMultiplier;   // 1.0-new; LIVE under Custom

                // difficulty is an enum FIELD on EA 0.11 (was property-shaped under
                // interop) — property-or-field reflection keeps the type-name independence.
                var diffMember = MPReflect.PropertyOrField(typeof(GameVariables), "difficulty");
                var diffType   = MPReflect.TypeOf(diffMember);
                if (diffMember != null && diffType != null && !string.IsNullOrEmpty(dto.Difficulty))
                    MPReflect.Set(diffMember, gv, Enum.Parse(diffType, dto.Difficulty));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Server] BuildGameVariables: {ex.Message}");
            }
            return gv;
        }

        /// <summary>H-FRESH-1: a LOADED world has no lobby DTO; this describes the live GameVariables so first-time
        /// joiners are born with the world's own start settings, not the Normal preset.</summary>
        public static GameVariablesDto DtoFromGameVariables(GameVariables gv)
        {
            var dto = new GameVariablesDto();
            try
            {
                dto.StartingAge                       = gv.startingAge;
                dto.DisableAging                      = gv.disableAging;
                // BuildGameVariables ORs the drain dial into this flag on the way in; read it back
                // plainly (the live dials follow below and carry the 0%-drain case themselves).
                dto.DisableEnergy                     = gv.disableEnergy;
                dto.DisableHappiness                  = gv.disableHappiness;
                dto.AllCoursesUnlocked                = gv.allCoursesUnlocked;
                dto.StartingMoney                     = gv.startingMoney;
                dto.TaxPercentage                     = gv.taxPercentage;
                dto.DaysPerYear                       = gv.daysPerYear;
                dto.MarketPriceMultiplier             = gv.marketPriceMultiplier;
                dto.EmployeeHourlySalaryMultiplier    = gv.employeeHourlySalaryMultiplier;
                dto.BankInterestMultiplier            = gv.bankInterestMultiplier;
                dto.TutorialEnabled                   = gv.tutorialEnabled;
                // gv.bankInterestRate — not written by BuildGameVariables (1.0 banking overhaul), so nothing to read back
                dto.RivalsDifficultyMultiplier        = gv.rivalsDifficultyMultiplier;
                dto.DisableVehicleDamage              = gv.disableVehicleDamage;
                dto.DisableVehicleFuel                = gv.disableVehicleFuel;
                dto.AllContactsUnlocked               = gv.allContactsUnlocked;
                dto.BaseCustomerPromotionMultiplier   = gv.baseCustomerPromotionMultiplier;
                dto.WholesaleUrgentFeeMultiplier      = gv.wholesaleUrgentFeeMultiplier;
                dto.ImporterUrgentFeeMultiplier       = gv.importerUrgentFeeMultiplier;
                dto.DisableWholesaleAndImportLimits   = gv.disableWholesaleAndImportLimits;
                dto.AllProductsAvailableFromImporters = gv.allProductsAvailableFromImporters;
                dto.ExportMultiplier                  = gv.exportMultiplier;
                dto.SellingMultiplier                 = gv.sellingMultiplier;   // 1.0-new; LIVE under Custom

                // difficulty: read back through the SAME property-or-field member BuildGameVariables writes.
                var diffMember = MPReflect.PropertyOrField(typeof(GameVariables), "difficulty");
                dto.Difficulty = MPReflect.Get(diffMember, gv)?.ToString() ?? dto.Difficulty;

                // BuildGameVariables pushes the needs dials OUT through MPNeedsTuning.Apply(dto);
                // the reverse reads the live ones back.
                dto.NeedsDrainPercent  = MPNeedsTuning.DrainPercent;
                dto.RestSpeedPercent   = MPNeedsTuning.RestPercent;
                dto.MoraleTempoPercent = MPNeedsTuning.MoralePercent;
                dto.PowerNapAllowed    = MPNeedsTuning.PowerNapAllowed;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Server] DtoFromGameVariables: {ex.Message}");
            }
            return dto;
        }

        /// <summary>Default multiplayer GameVariables — used for fallback (no-save) paths.</summary>
        public static GameVariables MakeGameVariables() => BuildGameVariables(Preset("Normal"));

        /// <summary>Host setter for the enforce-starting-cash toggle — re-broadcasts the lobby.</summary>
        public static void SetEnforceStartingCash(bool enforce)
        {
            EnforceStartingCash = enforce;
            if (_running && IsInLobby) BroadcastLobbyUpdate();
            Plugin.Logger.LogInfo($"[Server] Enforce starting cash = {enforce}");
        }

        private static void HandleRentRequest(MPLink peer, string senderPid, MessageEnvelope env)
        {
            var req = env.GetPayload<BuildingOwnershipPayload>();
            if (req == null) return;
            if (!SenderIs(req.OwnerPlayerId, senderPid, MessageType.RentRequest, allowEmpty: true)) return;

            Plugin.Logger.LogInfo($"[Server] RentRequest: {req.AddressKey} by {senderPid}");

            // Round-50 (bug 2026-07-21-204010): the whole grant/deny decision moves to the MAIN
            // THREAD — the new availability check reads the host's live game state, and the
            // enqueue ordering serialises racing requests for the same address (the off-thread
            // check-then-stamp was racy anyway).
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
#if BAMP_DEV
                    // Round-260 test switch ('rentdeny arm'): force the deny path so the
                    // client's optimistic-rent rollback can be exercised on demand.
                    if (TestDrive.ForceRentDeny)
                    {
                        req.DenyReason = "TestDrive forced deny (round-260 test)";
                        Send(peer, MessageEnvelope.Create(MessageType.RentDeny, "host", req));
                        Plugin.Logger.LogWarning($"[Server] Rent denied — {req.AddressKey}: TestDrive forced deny.");
                        return;
                    }
#endif
                    // (1) Player ledger — some player already holds it.
                    if (BuildingOwners.TryGetValue(req.AddressKey, out var currentOwner) && currentOwner != "")
                    {
                        req.DenyReason = $"already owned by {currentOwner}";
                        Send(peer, MessageEnvelope.Create(MessageType.RentDeny, "host", req));
                        Plugin.Logger.LogInfo($"[Server] Rent denied — {req.AddressKey} already owned by {currentOwner}");
                        return;
                    }

                    // (2) The HOST'S GAME is the authority on availability, not just the player
                    // ledger. A diverged client can rent a building its world wrongly shows as
                    // empty (field: an address the host ran a rival business on) — granting then
                    // stamped the ledger onto an occupied building: a permanently conflicted
                    // record every ownership sync re-fought, and the contaminated player stamp
                    // made the overtake blocker treat the AI shop as a player's.
                    string occupied = GameStatePatcher.HostRentUnavailableReason(req.AddressKey);
                    if (occupied != "")
                    {
                        req.DenyReason = occupied;
                        Send(peer, MessageEnvelope.Create(MessageType.RentDeny, "host", req));
                        Plugin.Logger.LogWarning($"[Server] Rent denied — {req.AddressKey} not available on the host: {occupied} (requester '{senderPid}' likely has a diverged world copy).");
                        return;
                    }

                    // (3) RIVAL-FAIR-2 R3c: the building belongs to a rival whose war is ON.  The
                    // game's own gate is CLIENT-SIDE (BizManPresentation.cs:542-555 / :755-768 reads
                    // GetSpecialRivalState(...).isActive before it even sends the rent message, and shows
                    // the game's own notification_cannot_rent_building_owned_by_rival), and it reads the
                    // LOCAL specialRivalStates - which only R3 keeps current.  This is the authority's
                    // own copy of the same test, through the EXISTING deny path and its existing reason
                    // plumbing (DenyReason travels to MPClient.HandleRentDeny :1422-1432, which logs it
                    // and rolls the optimistic local rent back - no new on-screen text).
                    // NOTE (corrected): the field native reads here is `buildingOwnerRivalId`
                    // (BuildingRegistration.cs:113) - who owns the BUILDING.  It DOES exist; the earlier
                    // note claiming otherwise was wrong.  `businessOwnerRivalId` (:115) is a different
                    // thing: the rival RUNNING the shop inside the building (a rival's shop may sit in a
                    // building another rival owns), and the mod also stamps PLAYER pids into that field -
                    // so it is the wrong field for this gate and would deny rents it should not.
                    try
                    {
                        var rivalReg = GameStatePatcher.FindRegistration(req.AddressKey);
                        if (BigAmbitions.Rivals.RivalsHelper.IsFeatureEnabled && rivalReg != null && !rivalReg.BuildingOwnedByPlayer)
                        {
                            var buildingRival = BigAmbitions.Rivals.RivalsHelper.GetSpecialRival(rivalReg.buildingOwnerRivalId);
                            string activeRivalId = "";
                            try { activeRivalId = buildingRival?.rivalData?.id ?? ""; } catch { }
                            if (buildingRival != null && activeRivalId.Length > 0
                                && BigAmbitions.Rivals.RivalsHelper.GetSpecialRivalState(activeRivalId)?.isActive == true)
                            {
                                req.DenyReason = "owned by an active rival";
                                Send(peer, MessageEnvelope.Create(MessageType.RentDeny, "host", req));
                                Plugin.Logger.LogInfo($"[RivalSync] rent of '{req.AddressKey}' by '{senderPid}' refused at the host: the building's rival '{activeRivalId}' is active.");
                                return;
                            }
                        }
                    }
                    catch (Exception rvx) { Plugin.Logger.LogWarning($"[RivalSync] rent rival gate for '{req.AddressKey}': {rvx.Message}"); }

                    // Grant it — to the CONNECTION's verified player, never a payload claim.
                    BuildingOwners[req.AddressKey] = senderPid;
                    req.OwnerPlayerId = senderPid;

                    // Confirm to the OTHER clients (not the requester — it already rented
                    // locally, so re-applying would double-charge it).  They mark it taken.
                    var confirm = MessageEnvelope.Create(MessageType.RentConfirm, "host", req);
                    foreach (var p in _clients.Keys)
                        if (p != peer) Send(p, confirm);
                    Plugin.Logger.LogInfo($"[Server] Rent confirmed: {req.AddressKey} → {senderPid} (relayed to {_clients.Count - 1} other client(s)).");
                    RefreshBuildingAccess();   // housing: a guest granted housing can now enter this newly-rented building

                    // Reflect it in the HOST's own game: take the building off the for-rent
                    // pool + mark it owned by that player (already on the main thread here).
                    GameStatePatcher.HostReflectPlayerRent(req.AddressKey, senderPid);
                    // (No event-driven save here: ownership is already tracked live on the
                    //  host, and cash is live-streamed — so a crash right after a purchase
                    //  loses neither.  Business internals ride the periodic autosave.)
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] RentRequest apply for '{req.AddressKey}': {ex.Message}"); }
            });
        }

        // Symmetric to HandleRentRequest: a client terminated a building's lease on
        // its own machine.  Release the host's authoritative ownership (so the host
        // stops re-asserting the rental onto that client), reflect the vacate in the
        // host's own game, and tell every client the building is free again.
        private static void HandleVacateRequest(MPLink peer, string senderPid, MessageEnvelope env)
        {
            var req = env.GetPayload<BuildingOwnershipPayload>();
            if (req == null) return;
            if (!SenderIs(req.OwnerPlayerId, senderPid, MessageType.VacateRequest, allowEmpty: true)) return;

            // Only the building's current owner may vacate it.  Unknown owner ⇒ allow
            // (the dict can be rebuilt across a reconnect; the client genuinely
            // unrented on its own machine, so honour it).
            if (BuildingOwners.TryGetValue(req.AddressKey, out var current) && current != "" && current != senderPid)
            {
                Plugin.Logger.LogWarning($"[Server] VacateRequest {req.AddressKey} from {senderPid} denied — owned by {current}.");
                return;
            }

            Plugin.Logger.LogInfo($"[Server] VacateRequest: {req.AddressKey} by {senderPid} — releasing.");
            BuildingOwners.TryRemove(req.AddressKey, out _);

            string addr = req.AddressKey;
            GameStatePatcher.EnqueueOnMainThread(() => GameStatePatcher.HostReflectPlayerVacate(addr));
            BroadcastVacate(req.AddressKey);   // tell every client it's available again
            RefreshBuildingAccess();           // housing: drop guests' access to this now-vacated building
        }

        // Symmetric to HandleRentRequest, for BOUGHT real estate.  The client already
        // bought optimistically on its own machine; the host is the single registrar of
        // ownership, so it arbitrates: if someone else already owns this building, deny
        // (the client rolls back).  Otherwise record the owner + remove the building from
        // the host's authoritative for-sale market — the for-sale poll then broadcasts
        // the removal, so no other player can buy it.
        private static void HandleBuyRequest(MPLink peer, string senderPid, MessageEnvelope env)
        {
            var req = env.GetPayload<BuildingOwnershipPayload>();
            if (req == null) return;
            if (!SenderIs(req.OwnerPlayerId, senderPid, MessageType.BuyRequest, allowEmpty: true)) return;

            if (BuildingRealEstateOwners.TryGetValue(req.AddressKey, out var current) && current != "" && current != senderPid)
            {
                Plugin.Logger.LogWarning($"[Server] BuyRequest {req.AddressKey} from {senderPid} DENIED — already owned by {current}.");
                Send(peer, MessageEnvelope.Create(MessageType.BuyDeny, "host", req));
                return;
            }

            BuildingRealEstateOwners[req.AddressKey] = senderPid;
            Plugin.Logger.LogInfo($"[Server] BuyRequest: {req.AddressKey} → owned by {senderPid}.");
            string addr = req.AddressKey;
            GameStatePatcher.EnqueueOnMainThread(() => GameStatePatcher.HostRemoveFromForSale(addr));
            RefreshBuildingAccess();   // housing: a guest granted housing can now enter this newly-bought building
        }

        /// <summary>True iff senderPid is the recorded owner of this address in EITHER ownership ledger
        /// (BuildingOwners = rents/operates, BuildingRealEstateOwners = bought).  The gate for inbound
        /// mutations that must come from the building's owner; empty / "host" / a different owner → false.
        /// Clients only ever send for shops they run, so requiring positive ownership rejects nothing
        /// legitimate: owner-push is self-healing (periodic re-assert), so a mutation that lands in the
        /// brief just-rented window before ownership is recorded is simply re-sent and accepted then.</summary>
        private static bool SenderOwns(string addr, string senderPid)
        {
            if (string.IsNullOrEmpty(addr) || string.IsNullOrEmpty(senderPid)) return false;
            if (BuildingOwners.TryGetValue(addr, out var o) && o == senderPid) return true;
            if (BuildingRealEstateOwners.TryGetValue(addr, out var r) && r == senderPid) return true;
            return false;
        }

        // ── Round-61: orphaned-tenancy reconcile (RED ROC factory, 2026-07-23) ─────
        // A 0.1.12-era ledger hole never heals on its own: the owner's machine runs a
        // LIVING business while the host's ledger has no entry for the building — so
        // every push is dropped forever ("sender doesn't own it") and the two worlds
        // stay permanently diverged (field: '9 twentysecondstreet', a factory with 19
        // staff on the owner's side vs an empty unowned shell on the host's; 42 drops
        // in one session; the client's own match check showed exactly 1 mismatch of
        // 825 addresses). When a business push claims a building the ledger knows
        // NOTHING about, verify on the main thread that the host's side is completely
        // VIRGIN — no rent owner, no deed owner, not the host's own tenancy, no AI
        // tenant, no live business type — and if so ADOPT the sender as tenant exactly
        // like a rent confirm would (ledger entry + HostReflectPlayerRent + access
        // refresh) and apply the push. The guards mean adoption can never take a
        // building from ANYONE: it only fills holes where the host has nothing and the
        // claimant has a living business. The next coordinated save persists the entry
        // (BuildOwnersStableKeyed); a crash before that just re-adopts next session.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _adoptNextCheckMs = new();
        private const int AdoptRecheckMs = 120_000;   // owner re-asserts periodically; one check per 2 min is plenty

        private static void TryAdoptOrphanedTenancy(BusinessInfo info, string senderPid)
        {
            try
            {
                string addr = info?.AddressKey ?? "";
                if (string.IsNullOrEmpty(addr) || string.IsNullOrEmpty(senderPid)) return;
                // The push must itself claim a LIVING business — an empty/typeless
                // push can't adopt anything.
                string type = info.BusinessTypeName ?? "";
                if (type.Length == 0 || type.EndsWith("_empty", StringComparison.Ordinal)) return;
                // The ledger must know NOTHING about the building — a claim by anyone
                // (including a renamed pid) stays the normal drop above.
                if (BuildingOwners.TryGetValue(addr, out var o) && !string.IsNullOrEmpty(o)) return;
                if (BuildingRealEstateOwners.TryGetValue(addr, out var r) && !string.IsNullOrEmpty(r)) return;
                // Per-address throttle (poll thread — TickCount wrap-safe comparison).
                int now = Environment.TickCount;
                if (_adoptNextCheckMs.TryGetValue(addr, out var next) && now - next < 0) return;
                _adoptNextCheckMs[addr] = now + AdoptRecheckMs;
                var pushed = info; string pid = senderPid;
                GameStatePatcher.EnqueueOnMainThread(() =>
                {
                    try
                    {
                        // Re-check the ledgers on the main thread (a rent/buy could have
                        // landed while this was queued).
                        if (BuildingOwners.TryGetValue(addr, out var o2) && !string.IsNullOrEmpty(o2)) return;
                        if (BuildingRealEstateOwners.TryGetValue(addr, out var r2) && !string.IsNullOrEmpty(r2)) return;
                        var reg = GameStatePatcher.FindRegistration(addr);
                        if (reg == null) return;
                        bool hostRents = false; try { hostRents = reg.RentedByPlayer; } catch { }
                        if (hostRents) return;                                     // the host's own shop — never
                        string tenant = ""; try { tenant = reg.businessOwnerRivalId ?? ""; } catch { }
                        if (tenant.Length > 0 && tenant != pid && !GameStatePatcher.IsSessionPlayerId(tenant)) return;   // an AI runs a business here
                        string hostType = ""; try { hostType = reg.businessTypeName ?? ""; } catch { }
                        if (hostType.Length > 0 && !hostType.EndsWith("_empty", StringComparison.Ordinal)) return;       // host sees a live business
                        BuildingOwners[addr] = pid;
                        GameStatePatcher.HostReflectPlayerRent(addr, pid);
                        RefreshBuildingAccess();
                        Plugin.Logger.LogWarning($"[Ledger] ADOPTED orphaned tenancy: '{addr}' → '{pid}' — the owner's machine runs a live business ('{pushed.BusinessName}', {pushed.BusinessTypeName}) but the host ledger had NO entry and the host's world showed an unowned empty shell (0.1.12-era hole; round-61). This building's pushes are accepted from now on.");
                        GameStatePatcher.ApplyClientBusinessChange(pushed);
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Ledger] orphan adopt '{addr}': {ex.Message}"); }
                });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Ledger] orphan adopt: {ex.Message}"); }
        }

        // A client listed an owned building for sale.  Add it to the host's authoritative
        // for-sale market (with the seller's price); the for-sale poll then broadcasts it.
        private static void HandleListForSale(MPLink peer, string senderPid, MessageEnvelope env)
        {
            var info = env.GetPayload<BuildingForSaleInfo>();
            if (info == null || string.IsNullOrEmpty(info.AddressKey)) return;
            if (!SenderOwns(info.AddressKey, senderPid))
            {
                LogRejectThrottled("ListForSale", info.AddressKey, $"from '{senderPid}' but the sender doesn't own it");
                return;
            }
            Plugin.Logger.LogInfo($"[Server] ListForSale: {info.AddressKey} by {senderPid} @ {info.BuildingPrice:F0}.");
            GameStatePatcher.EnqueueOnMainThread(() => GameStatePatcher.HostAddToForSale(info));
        }

        // A client canceled its building's sale.  Remove it from the host's market.
        private static void HandleCancelSale(MPLink peer, string senderPid, MessageEnvelope env)
        {
            var req = env.GetPayload<BuildingOwnershipPayload>();
            if (req == null || string.IsNullOrEmpty(req.AddressKey)) return;
            if (!SenderOwns(req.AddressKey, senderPid))
            {
                LogRejectThrottled("CancelSale", req.AddressKey, $"from '{senderPid}' but the sender doesn't own it");
                return;
            }
            Plugin.Logger.LogInfo($"[Server] CancelSale: {req.AddressKey} by {senderPid}.");
            string addr = req.AddressKey;
            GameStatePatcher.EnqueueOnMainThread(() => GameStatePatcher.HostRemoveFromForSale(addr));
        }

        // The AI bought a client's listed building (completed in the client's own sim).
        // Drop it from the host's authoritative market + clear the ownership registry.
        private static void HandleSaleCompleted(MPLink peer, string senderPid, MessageEnvelope env)
        {
            var req = env.GetPayload<BuildingOwnershipPayload>();
            if (req == null || string.IsNullOrEmpty(req.AddressKey)) return;
            if (!SenderOwns(req.AddressKey, senderPid))
            {
                Plugin.Logger.LogWarning($"[Server] SaleCompleted for '{req.AddressKey}' from '{senderPid}' but the sender doesn't own it — dropped.");
                return;
            }
            Plugin.Logger.LogInfo($"[Server] SaleCompleted: {req.AddressKey} (AI bought {senderPid}'s building) — releasing.");
            BuildingRealEstateOwners.TryRemove(req.AddressKey, out _);
            string addr = req.AddressKey;
            GameStatePatcher.EnqueueOnMainThread(() => GameStatePatcher.HostRemoveFromForSale(addr));
        }

        // ── Message handlers (cont.) ──────────────────────────────────────────

        // ── Appearance sync ───────────────────────────────────────────────────

        private static void HandleClientAppearance(string senderPid, MessageEnvelope env)
        {
            var dto = env.GetPayload<PlayerAppearancePayload>();
            if (dto == null || !SenderIs(dto.PlayerId, senderPid, MessageType.PlayerAppearance)) return;
            // Apply + re-broadcast on the main thread (touches Unity objects).
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                RemotePlayerManager.SetAppearance(dto);
                BroadcastAppearanceSync();
            });
        }

        /// <summary>Registers the host's own appearance and syncs the full set to clients.</summary>
        public static void RegisterHostAppearance(PlayerAppearancePayload dto)
        {
            RemotePlayerManager.SetAppearance(dto);
            BroadcastAppearanceSync();
        }

        /// <summary>Broadcasts every known player appearance to all clients.</summary>
        /// <summary>Peer-targeted appearance replay for a (re)joiner — appearances are host-authoritative
        /// but only broadcast on change, so a joiner would otherwise see default avatars until someone re-dresses.</summary>
        public static void SendAppearanceSyncTo(MPLink peer)
        {
            if (peer == null) return;
            Send(peer, MessageEnvelope.Create(MessageType.AppearanceSync, "host",
                new AppearanceSyncPayload { Players = RemotePlayerManager.GetAllAppearances() }));
        }

        /// <summary>Relay a shop's stock digest to every client (own shop digest, or a validated client one).</summary>
        public static void BroadcastStockDigest(ShopStockDigestPayload p)
        {
            if (!_running || p == null) return;
            Broadcast(MessageEnvelope.Create(MessageType.ShopStockDigest, "host", p));
        }

        // Round-41 (customer puppets): the host's simulator-election result / a puppet-state relay.
        // T8: authority election deliberately STAYS a broadcast-to-all — it is tiny, fires only on
        // occupancy changes, and a machine that just LEFT the building may need the "" revert.
        public static void BroadcastCustomerAuthority(CustomerSimAuthorityPayload p)
        {
            if (!_running || p == null) return;
            if (string.IsNullOrEmpty(p.SimulatorPid)) ClearLookCache(p.AddressKey);   // T8: nobody inside — customer set dissolves
            Broadcast(MessageEnvelope.Create(MessageType.CustomerSimAuthority, "host", p));
        }

        /// <summary>T8 (2026-08 throughput audit): the puppet-class streams were the mod's largest
        /// remaining steady flow (~7 KB/s measured with one busy shop) and went to EVERY client —
        /// players nowhere near the building included — plus an echo to the simulator itself.
        /// They describe what is visible INSIDE one building, so they go to exactly the players
        /// standing in it (the _bldgByPeer PRESENCE map — review B1: NOT the InteriorSync
        /// subscription, which owners never join), minus the sender. A new enterer converges
        /// from the 4 Hz absolute state within a beat; looks are cache-replayed on entry.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _bldgByPeer = new();

        private static void SendToBuildingSubscribers(MessageType type, string addressKey, object payload, int exceptPeerId)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            byte[]? data = null;   // serialize lazily — often nobody but the sender is inside
            foreach (var peer in _clients.Keys)
            {
                if (peer == null || peer.Id == exceptPeerId) continue;
                if (!_bldgByPeer.TryGetValue(peer.Id, out var b) || b != addressKey) continue;
                data ??= MessageEnvelope.Create(type, "host", payload).Serialize();
                peer.Send(data, reliable: true);
            }
        }

        public static void BroadcastCustomerPuppets(CustomerPuppetStatePayload p, int exceptPeerId = -1)
        {
            if (!_running || p == null) return;
            SendToBuildingSubscribers(MessageType.CustomerPuppetState, p.AddressKey, p, exceptPeerId);
        }

        /// <summary>Round-119: relay a serve beat so whoever is working that till performs it —
        /// T8: the duty player is by definition inside, so the subscriber set covers them.</summary>
        public static void BroadcastRegisterServe(RegisterServePayload p, int exceptPeerId = -1)
        {
            if (!_running || p == null) return;
            SendToBuildingSubscribers(MessageType.RegisterServe, p.AddressKey, p, exceptPeerId);
        }

        public static void BroadcastCustomerEmote(CustomerPuppetEmotePayload p, int exceptPeerId = -1)
        {
            if (!_running || p == null) return;
            SendToBuildingSubscribers(MessageType.CustomerPuppetEmote, p.AddressKey, p, exceptPeerId);
        }

        // ── T8 look cache: followers deliberately cache looks AHEAD of entering (round-44c
        // re-dress from _looksById) — with looks routed to inside-players only, the host keeps
        // the latest look per (building, customer) and replays them on subscribe. Cosmetic
        // data: capped per building, cleared when the building's customer set dissolves
        // (authority → "") and at Stop. Lock-guarded — written from poll (relay) and main
        // (host-simulator) threads alike.
        private const int LookCachePerBuilding = 80;
        private sealed class LookCacheEntry
        {
            public readonly Dictionary<string, CustomerPuppetLookPayload> Map = new();
            public readonly Queue<string> Order = new();   // insertion order — FIFO eviction
        }
        private static readonly Dictionary<string, LookCacheEntry> _lookCacheByAddr = new();

        private static void CacheLook(CustomerPuppetLookPayload p)
        {
            if (string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.CustomerId)) return;
            // Review MAJOR-2: the address is client-authored — cache only for REAL player
            // buildings (the only ones puppets exist for). Also bounds the outer dictionary.
            if (!BuildingOwners.ContainsKey(p.AddressKey)) return;
            lock (_lookCacheByAddr)
            {
                if (!_lookCacheByAddr.TryGetValue(p.AddressKey, out var e))
                    _lookCacheByAddr[p.AddressKey] = e = new LookCacheEntry();
                if (!e.Map.ContainsKey(p.CustomerId))
                {
                    e.Order.Enqueue(p.CustomerId);
                    // Review MAJOR-1: FIFO eviction, never a wholesale wipe — customer ids are
                    // never reused, so the oldest entries belong to shoppers long gone; a wipe
                    // took LIVE customers' looks with it and nothing re-ships those mid-episode.
                    while (e.Order.Count > LookCachePerBuilding) e.Map.Remove(e.Order.Dequeue());
                }
                e.Map[p.CustomerId] = p;
            }
        }

        internal static void ClearLookCache(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            lock (_lookCacheByAddr) _lookCacheByAddr.Remove(addressKey);
        }

        /// <summary>T8: replay the cached looks for one building to a peer that just walked in
        /// (POLL THREAD — called from HandlePlayerMove on the presence edge; peer.Send is
        /// thread-safe, established pattern). Note MINOR-1: each replayed look keeps its
        /// original SimulatorPid, and ApplyLook drops looks whose SimulatorPid is the receiver
        /// itself — a returning FORMER simulator discards its own replay. That is safe by
        /// MAJOR-1's rule: the current simulator's ship-once set clears per occupancy episode,
        /// so those looks re-ship on the next stream beat.</summary>
        internal static void SendCachedLooksTo(MPLink peer, string addressKey)
        {
            if (!_running || peer == null) return;
            try
            {
                List<CustomerPuppetLookPayload>? looks = null;
                lock (_lookCacheByAddr)
                {
                    if (_lookCacheByAddr.TryGetValue(addressKey, out var e) && e.Map.Count > 0)
                        looks = new List<CustomerPuppetLookPayload>(e.Map.Values);
                }
                if (looks == null) return;
                foreach (var l in looks)
                    peer.Send(MessageEnvelope.Create(MessageType.CustomerPuppetLook, "host", l));
                Plugin.Logger.LogInfo($"[Server] replayed {looks.Count} cached customer look(s) for '{addressKey}' to peer {peer.Id} (T8).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendCachedLooksTo: {ex.Message}"); }
        }

        public static void BroadcastCustomerLook(CustomerPuppetLookPayload p, int exceptPeerId = -1)
        {
            if (!_running || p == null) return;
            CacheLook(p);
            SendToBuildingSubscribers(MessageType.CustomerPuppetLook, p.AddressKey, p, exceptPeerId);
        }

        public static void BroadcastAppearanceSync()
        {
            if (!_running) return;
            var payload = new AppearanceSyncPayload { Players = RemotePlayerManager.GetAllAppearances() };
            Broadcast(MessageEnvelope.Create(MessageType.AppearanceSync, "host", payload));
            Plugin.Logger.LogInfo($"[Server] AppearanceSync broadcast ({payload.Players.Count} players).");
        }

        private static void HandlePlayerMove(MPLink sender, string senderPid, MessageEnvelope env)
        {
            var payload = env.GetPayload<PlayerPositionPayload>();
            if (payload == null || !SenderIs(payload.PlayerId, senderPid, MessageType.PlayerMove)) return;

            // T8 review B1: the puppet-routing PRESENCE map — which building each peer's player
            // is standing in, from the same Bldg field the simulator election reads. Poll-thread
            // owned, self-healing every position tick, pruned at disconnect. (The InteriorSync
            // subscription CANNOT serve this purpose: a building's owner never subscribes — the
            // subscription replicates buildings to non-owners — yet owners are exactly who the
            // puppet feature serves.)
            string newBldg = payload.Bldg ?? "";
            _bldgByPeer.TryGetValue(sender.Id, out var oldBldg);
            if (newBldg != (oldBldg ?? ""))
            {
                if (newBldg.Length == 0) _bldgByPeer.TryRemove(sender.Id, out _);
                else
                {
                    _bldgByPeer[sender.Id] = newBldg;
                    // Entering a building = replay its cached customer looks (followers dress
                    // puppets from a look cache they used to fill via the old broadcast-to-all).
                    SendCachedLooksTo(sender, newBldg);
                }
            }

            // Show this player on the host's own screen
            GameStatePatcher.EnqueueOnMainThread(() =>
                RemotePlayerManager.SpawnOrUpdate(payload));

            // Relay to every other connected client
            var bytes = env.Serialize();
            foreach (var peer in _clients.Keys)
                if (peer.Id != sender.Id)
                    peer.Send(bytes, reliable: false);
        }

        private static void HandleAnimTrigger(MPLink sender, string senderPid, MessageEnvelope env)
        {
            var payload = env.GetPayload<AnimTriggerPayload>();
            if (payload == null || !SenderIs(payload.PlayerId, senderPid, MessageType.PlayerAnimTrigger)) return;
            if (payload.ParamIndex < 0 || payload.ParamIndex > 255) return;   // animator params number in the dozens

            // Play on the host's own screen
            GameStatePatcher.EnqueueOnMainThread(() =>
                RemotePlayerManager.ApplyTrigger(payload.PlayerId, payload.ParamIndex));

            // Relay to every other connected client (reliable — triggers are one-off)
            var bytes = env.Serialize();
            foreach (var peer in _clients.Keys)
                if (peer.Id != sender.Id)
                    peer.Send(bytes, reliable: true);
        }

        /// <summary>Authoritative market-events list → all clients.</summary>
        public static void BroadcastMarketEvents(string json)
        {
            if (!_running || string.IsNullOrEmpty(json)) return;
            Broadcast(MessageEnvelope.Create(MessageType.MarketEvents, "host",
                new MarketEventsPayload { Json = json }));
        }

        /// <summary>Join replay (anti-pattern Class 4): send the current market events to ONE connecting peer.
        /// MarketEvents are broadcast only on CHANGE (hash-gated), so without this a hot-joiner would see no
        /// active shortages/hype until the event set next changes — which may be never. Call on the main thread
        /// (reads gi); the on-connect senders already are.</summary>
        public static void SendMarketEventsTo(MPLink peer)
        {
            if (!_running || peer == null) return;
            try
            {
                var events = SaveGameManager.Current?.marketEvents;
                if (events == null || events.Count == 0) return;
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(events);
                Send(peer, MessageEnvelope.Create(MessageType.MarketEvents, "host",
                    new MarketEventsPayload { Json = json }));
                Plugin.Logger.LogInfo($"[Server] Sent {events.Count} market event(s) to peer {peer.Id} (join replay).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendMarketEventsTo: {ex.Message}"); }
        }

        /// <summary>Tell one client to log its per-registration audit hashes for
        /// the diverged buckets (the host logs its own side; diff offline).</summary>
        public static void SendAuditDrill(string playerId, List<int> buckets)
        {
            if (!_running || buckets == null || buckets.Count == 0) return;
            foreach (var peer in _clients.Keys)
            {
                if (!_peerNames.TryGetValue(peer.Id, out var pid) || pid != playerId) continue;
                Send(peer, MessageEnvelope.Create(MessageType.AuditDrill, "host",
                    new AuditDrillPayload { Buckets = buckets }));
                break;
            }
        }

        /// <summary>Broadcasts the host's own animator trigger to all clients.</summary>
        public static void BroadcastAnimTrigger(int paramIndex)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(MessageType.PlayerAnimTrigger, MPConfig.PlayerId,
                new AnimTriggerPayload { PlayerId = MPConfig.PlayerId, ParamIndex = paramIndex }));
        }

        private static void HandleVehicleSync(MPLink sender, string senderPid, MessageEnvelope env)
        {
            var payload = env.GetPayload<VehicleFleetPayload>();
            if (payload == null || !SenderIs(payload.OwnerId, senderPid, MessageType.VehicleSync)) return;
            if (payload.Vehicles.Count > 200)
            {
                Plugin.Logger.LogWarning($"[Server] VehicleSync from '{senderPid}': implausible fleet size {payload.Vehicles.Count} — dropped.");
                return;
            }

            // Apply on the host's own screen + record ownership for passenger eligibility.
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                PassengerSync.NoteFleet(payload);
                VehicleManager.ApplyVehicleFleet(payload);
            });

            // Relay to every other connected client
            var bytes = env.Serialize();
            foreach (var peer in _clients.Keys)
                if (peer.Id != sender.Id)
                    peer.Send(bytes, reliable: true);
        }

        /// <summary>Broadcasts the host's own vehicle fleet to all clients.</summary>
        public static void BroadcastVehicleSync(VehicleFleetPayload payload)
        {
            if (!_running) return;
            PassengerSync.NoteFleet(payload);   // host owns these (records owner + type)
            Broadcast(MessageEnvelope.Create(MessageType.VehicleSync, MPConfig.PlayerId, payload));
        }

        // Cars currently being driven by a borrower (vehicleId → last VehicleDrive time) so HostCanBoard lets
        // the OWNER ride their own car as a passenger while it's borrowed.
        // Round-232b: driver-aware + thread-safe (the grace check below reads it on the POLL thread;
        // Environment.TickCount instead of Time.unscaledTime because Unity time is main-thread-only).
        private sealed class DrivenRec { public string Driver = ""; public int TickMs; }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DrivenRec> _drivenCars = new();
        public static bool IsCarDriven(string vid)
            => !string.IsNullOrEmpty(vid) && _drivenCars.TryGetValue(vid, out var r) && unchecked(Environment.TickCount - r.TickMs) < 2000;

        private static void HandleVehicleDrive(string senderPid, MessageEnvelope env)
        {
            var p = env.GetPayload<VehicleDrivePayload>();
            if (p == null || !SenderIs(p.DriverId, senderPid, MessageType.VehicleDrive)) return;
            if (!GrantSync.IsGranted(p.OwnerId, senderPid))
            {
                // Round-232b (user-approved): a mid-drive revoke must not cut the stream — the despawn
                // guard (231a) lets the borrower finish the drive, so the owner's follow has to keep
                // tracking it or the car freezes at the revoke pose and teleports at hand-back (rig,
                // 2026-08-11). Grace: the vid's CURRENT driver stays accepted until Released or 5s of
                // silence. A never-granted sender can't enter the table (this gate ran first).
                bool grace = _drivenCars.TryGetValue(p.VehicleId, out var g)
                             && g.Driver == senderPid
                             && unchecked(Environment.TickCount - g.TickMs) < 5000;
                if (!grace) return;
                if (p.Released)
                    Plugin.Logger.LogInfo($"[Drive] revoked driver '{senderPid}' handed back '{p.VehicleId}' — grace stream ends.");
            }
            if (p.Released) _drivenCars.TryRemove(p.VehicleId, out _);
            else _drivenCars[p.VehicleId] = new DrivenRec { Driver = senderPid, TickMs = Environment.TickCount };
            GameStatePatcher.EnqueueOnMainThread(() => VehicleManager.ApplyDriveSync(p));   // host applies (it may be the owner)
            Broadcast(MessageEnvelope.Create(MessageType.VehicleDrive, MPConfig.PlayerId, p));   // to all (the driver ignores its echo)
        }

        /// <summary>Host-as-driver: broadcast the pose of a borrowed car the host is driving.</summary>
        public static void BroadcastVehicleDrive(VehicleDrivePayload p)
        {
            if (!_running || p == null) return;
            Broadcast(MessageEnvelope.Create(MessageType.VehicleDrive, MPConfig.PlayerId, p));
        }

        // ── Passenger (ride shotgun) — host-authoritative ─────────────────────
        private static void HandlePassengerBoardRequest(string senderPid, MessageEnvelope env)
        {
            var p = env.GetPayload<PassengerBoardRequestPayload>();
            if (p == null || !SenderIs(p.PlayerId, senderPid, MessageType.PassengerBoardRequest)) return;
            GameStatePatcher.EnqueueOnMainThread(() => ResolveBoard(p.PlayerId, p.VehicleId));
        }

        /// <summary>Host: validate + apply a board, then broadcast the authoritative result.
        /// Main thread (PassengerSync is single-threaded). Used for client requests AND the
        /// host's own player (HostBoardRequest).</summary>
        private static void ResolveBoard(string playerId, string vehicleId)
        {
            var res = new PassengerBoardResultPayload { PlayerId = playerId, VehicleId = vehicleId };
            // Release any seat this player already holds BEFORE choosing one, so a re-board (even if
            // a prior exit was missed on the host) frees their stale seat and they get the FRONT seat
            // again, not the next one down — the "passenger lands in the rear-left" bug. The result
            // broadcast below re-syncs every client (their ApplyBoard releases the old seat too).
            PassengerSync.ApplyExit(playerId);
            if (PassengerSync.HostCanBoard(vehicleId, playerId, out int seat, out string reason))
            {
                res.Seat = seat;
                PassengerSync.ApplyBoard(vehicleId, playerId, seat);
            }
            else
            {
                res.Seat = -1; res.Reason = reason;
                if (playerId == MPConfig.PlayerId) PassengerHud.ToastReason(reason);   // host's own click → feedback
            }
            Broadcast(MessageEnvelope.Create(MessageType.PassengerBoardResult, MPConfig.PlayerId, res));
        }

        private static void HandlePassengerExit(string senderPid, MessageEnvelope env)
        {
            var p = env.GetPayload<PassengerExitPayload>();
            if (p == null || !SenderIs(p.PlayerId, senderPid, MessageType.PassengerExit)) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                PassengerSync.ApplyExit(p.PlayerId);
                Broadcast(MessageEnvelope.Create(MessageType.PassengerExit, MPConfig.PlayerId, p));
            });
        }

        /// <summary>Host → a rider: follow the driver into a building (the vehicle they're riding drove in).</summary>
        public static void SendPassengerFollowEnter(string targetPid, string addressKey, string vehicleId)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(MessageType.PassengerFollowEnter, MPConfig.PlayerId,
                new PassengerFollowPayload { TargetPlayerId = targetPid, AddressKey = addressKey, VehicleId = vehicleId }));
        }

        /// <summary>Host → a rider: follow the driver back OUT of a building (the vehicle they're riding drove out).</summary>
        public static void SendPassengerFollowExit(string targetPid, string vehicleId, int exitId)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(MessageType.PassengerFollowExit, MPConfig.PlayerId,
                new PassengerFollowPayload { TargetPlayerId = targetPid, VehicleId = vehicleId, ExitId = exitId }));
        }

        /// <summary>Host: resolve vehicle V's riders and tell each (except the driver) to follow the driver
        /// INTO a building — broadcast to client riders, or act locally when the host itself is the rider.
        /// One path for both the host-as-driver hook and the client-as-driver relay.</summary>
        public static void RouteFollowEnterToRiders(string vehicleId, string addressKey, string driverPid)
        {
            if (!_running || string.IsNullOrEmpty(vehicleId)) return;
            var riders = PassengerSync.RidersOf(vehicleId);
            if (riders == null || riders.Count == 0) return;
            foreach (var kv in riders)
            {
                string pid = kv.Value;
                if (string.IsNullOrEmpty(pid) || pid == driverPid) continue;   // the driver isn't a passenger
                if (pid == MPConfig.PlayerId)
                    GameStatePatcher.EnqueueOnMainThread(() => PassengerRide.FollowDriverIntoByAddress(addressKey));   // host is the rider
                else
                    SendPassengerFollowEnter(pid, addressKey, vehicleId);   // client rider → broadcast; it acts
            }
        }

        /// <summary>Host: tell vehicle V's riders (except the driver) to follow the driver back OUT.</summary>
        public static void RouteFollowExitToRiders(string vehicleId, int exitId, string driverPid)
        {
            if (!_running || string.IsNullOrEmpty(vehicleId)) return;
            var riders = PassengerSync.RidersOf(vehicleId);
            if (riders == null || riders.Count == 0) return;
            foreach (var kv in riders)
            {
                string pid = kv.Value;
                if (string.IsNullOrEmpty(pid) || pid == driverPid) continue;
                if (pid == MPConfig.PlayerId)
                    GameStatePatcher.EnqueueOnMainThread(() => PassengerRide.FollowDriverOut(exitId));   // host is the rider
                else
                    SendPassengerFollowExit(pid, vehicleId, exitId);
            }
        }

        /// <summary>Client(driver) → host relay: the sender drove V into a building; fan out to V's riders.</summary>
        private static void HandlePassengerFollowRelayEnter(string senderPid, MessageEnvelope env)
        {
            var p = env.GetPayload<PassengerFollowPayload>();
            if (p == null) return;
            RouteFollowEnterToRiders(p.VehicleId, p.AddressKey, senderPid);
        }

        /// <summary>Client(driver) → host relay: the sender drove V out of a building; fan out to V's riders.</summary>
        private static void HandlePassengerFollowRelayExit(string senderPid, MessageEnvelope env)
        {
            var p = env.GetPayload<PassengerFollowPayload>();
            if (p == null) return;
            RouteFollowExitToRiders(p.VehicleId, p.ExitId, senderPid);
        }

        /// <summary>Phase 3: a client uploaded its pending disconnect save. Validate its ACTUAL in-game day
        /// on the main thread (TryCommitClientDisconnectSave commits it iff valid), then send the real
        /// LoadData — the validated disconnect save if accepted, else our stored copy / fallback.</summary>
        private static void HandleClientDisconnectUpload(string senderPid, MessageEnvelope env)
        {
            var p = env.GetPayload<SaveDataPayload>();
            if (p == null) return;
            p.HsgRaw = env.Attachment;   // v9 rider → payload
            if (!StableIdByPlayer.TryGetValue(senderPid, out var stable) || string.IsNullOrEmpty(stable)) return;
            string session = MPSaveCoordinator.ActiveSessionName;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    MPSaveCoordinator.TryCommitClientDisconnectSave(p, stable);   // validates (game scanner) + stores iff valid
                    var peer = PeerForPid(senderPid);
                    if (peer != null) SendMidJoinLoadData(peer, senderPid, stable, session);
                    else Plugin.Logger.LogWarning($"[Server] ClientDisconnectUpload: no peer for '{senderPid}' — can't send load.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] HandleClientDisconnectUpload: {ex.Message}"); }
            });
        }

        /// <summary>Round-162: send an envelope to one connected player (no-op if offline).</summary>
        internal static void SendToPlayer(string pid, MessageEnvelope env)
        {
            var peer = PeerForPid(pid);
            if (peer != null) Send(peer, env);
            else Plugin.Logger.LogWarning($"[Server] SendToPlayer: '{pid}' is not connected — dropped {env?.Type}.");
        }

        /// <summary>Resolve the live MPLink for a player id (null if not connected).</summary>
        private static MPLink? PeerForPid(string pid)
        {
            foreach (var peer in _clients.Keys)
                if (_peerNames.TryGetValue(peer.Id, out var nm) && nm == pid) return peer;
            return null;
        }

        /// <summary>Send a joining/rejoining client its resolved session save: their stored .hsg if present,
        /// 'save-unavailable' if a slot exists but the file can't be read, else a fresh-character fallback.
        /// Used by the mid-join Hello path and after a validated disconnect-save upload.</summary>
        private static bool SendMidJoinLoadData(MPLink peer, string pid, string stable, string session)
        {
            if (peer == null || string.IsNullOrEmpty(session) || string.IsNullOrEmpty(stable)) return false;
            // Review-2 fix: READ from the folder that holds the current state (the loaded
            // variant until the first save, then the latest save target — carry-forward
            // has already placed a joiner's newest copy there). The client ADOPTS the
            // lineage BASE: its ongoing saves must never land in a frozen variant
            // (round-37 fork semantics). Serving from the base rolled a rejoining former
            // host back to the last manual save — or fresh-started them when the base
            // folder never existed (auto-only worlds).
            string source = MPSaveCoordinator.MidJoinSourceSession;
            if (string.IsNullOrEmpty(source)) source = session;
            string adopt = MPSaveCoordinator.StripAutoSuffix(session);
            // Round-184: BOTH serve paths resolve through the ONE ladder (exact session → lineage
            // rescue → unavailable → fresh) — the duplicated copies drifted and a fix landed in
            // only one.  A missing .hsg here would otherwise fresh-start a returning player,
            // whose now-authoritative empty save then blanks every building the ledger still
            // grants them, on BOTH machines (rig-proven, TEST184-INT2).
            var verdict = MPSaveCoordinator.ResolveMemberSave(source, stable, out var data, out var servedFrom, out var cash);
            if (data != null)
            {
                // Reclaim: ownership reserved under the absent owner's stableId re-keys to their live pid.
                int rekeyed = 0;
                foreach (var kv in BuildingOwners)
                    if (kv.Value == stable) { BuildingOwners[kv.Key] = pid; rekeyed++; }
                foreach (var kv in BuildingRealEstateOwners)   // symmetric: bought real estate reserved under the absent owner's stableId must also re-key to their live pid
                    if (kv.Value == stable) { BuildingRealEstateOwners[kv.Key] = pid; rekeyed++; }
                if (rekeyed > 0) Plugin.Logger.LogInfo($"[Server] Re-keyed {rekeyed} reserved building(s) to '{pid}'.");
                var m = MPSaveManager.ReadManifest(servedFrom);
                var midEnv = MessageEnvelope.Create(MessageType.LoadData, "host", new LoadDataPayload
                {
                    SessionName = adopt, RawLength = data.Value.raw, Money = cash,
                    MetaJson = MPSaveCoordinator.ReadMemberMetaJson(servedFrom, stable),   // round-275b: keep their copy datable
                    MoneyKnown = cash != 0f || GetKnownCash(pid) >= 0f,   // round-224: 0-and-unstreamed = unknown
                    // Handoff slice 4: identity/day/epoch for the joiner's log-only diagnostics.
                    WorldDay = m?.WorldDay ?? 0, PlaythroughId = !string.IsNullOrEmpty(m?.PlaythroughId) ? m.PlaythroughId : MPSaveCoordinator.ActivePlaythroughId, HostEpoch = m?.HostEpoch ?? 0,
                    LoadGen = MintLoadGen(pid),   // round-284 load ticket
                });
                midEnv.Attachment = data.Value.gz;   // v9: raw gzip rides the attachment frame
                Send(peer, midEnv);
                Plugin.Logger.LogInfo($"[Server] Mid-session join: sent LoadData to '{pid}' (source='{servedFrom}', adopt='{adopt}', {data.Value.raw}B, ${cash:F0}); world state follows once their scene loads.");
                return true;
            }
            if (verdict == MPSaveCoordinator.ServeVerdict.Unavailable)
            {
                Send(peer, MessageEnvelope.Create(MessageType.LoadData, "host", new LoadDataPayload
                { SessionName = adopt, HsgGzipBase64 = "", SaveUnavailable = true, FallbackSettings = LastStartSettings }));
                Plugin.Logger.LogError($"[Server] Mid-session join by '{pid}' (stable={stable}): has a save slot but its .hsg is unreadable — REFUSING to fresh-start. Sent save-unavailable.");
                return true;
            }
            float kc = GetKnownCash(pid);
            Send(peer, MessageEnvelope.Create(MessageType.LoadData, "host", new LoadDataPayload
            { SessionName = adopt, HsgGzipBase64 = "", Money = Math.Max(0f, kc), FallbackSettings = LastStartSettings,
              PlaythroughId = MPSaveCoordinator.ActivePlaythroughId,   // round-217: identity always travels
              LoadGen = MintLoadGen(pid) }));   // round-284 load ticket (fresh start = served load)
            // 4a diagnostic: fresh-starting someone who OWNS property is the "lost character" smoking gun —
            // they had a session presence but no save survived. Loud ERROR so a bug report pinpoints it;
            // genuinely-new joiners stay at INFO.
            bool ownsProperty = BuildingOwners.Values.Any(v => v == stable || v == pid)
                             || BuildingRealEstateOwners.Values.Any(v => v == stable || v == pid);
            if (ownsProperty)
                Plugin.Logger.LogError($"[Server] DATA-LOSS SUSPECT: fresh-starting '{pid}' (stable={stable}) who OWNS building(s) but has NO save on the host — character likely lost. Sent fresh-character fallback.");
            else
                Plugin.Logger.LogInfo($"[Server] Mid-session join by '{pid}': no save slot — sent fresh-character fallback.");
            return true;
        }

        private static void HandleVehicleLockSet(string senderPid, MessageEnvelope env)
        {
            var p = env.GetPayload<VehicleLockPayload>();
            if (p == null || !SenderIs(p.OwnerId, senderPid, MessageType.VehicleLockSet)) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                // Authorization reads the SENDER against the live tables — the payload's OwnerId is
                // only the spoof-checked sender identity (the guard above). The vehicle's OWNER or a
                // currently-granted KEY-HOLDER may toggle (ruling 2026-08-26: a granted key works
                // like real car keys — the widening from "only the real owner may lock").
                string realOwner = PassengerSync.OwnerOf(p.VehicleId);
                if (string.IsNullOrEmpty(realOwner))
                {
                    LogRejectThrottled("VehicleLock", p.VehicleId, $"from '{senderPid}' — owner unknown here (fleet not synced yet?)");
                    EchoLockTruth(senderPid, p.VehicleId, "");   // 1:1 reply, non-amplifying
                    return;
                }
                if (realOwner != senderPid && !GrantSync.IsGranted(realOwner, senderPid))
                {
                    LogRejectThrottled("VehicleLock", p.VehicleId, $"from '{senderPid}' — not the owner and no key");
                    EchoLockTruth(senderPid, p.VehicleId, realOwner);   // 1:1 reply, non-amplifying
                    return;
                }
                PassengerSync.SetLock(p.VehicleId, p.Locked);
                PassengerLockButton.RefreshFor(p.VehicleId);   // the host player may be sitting in it
                p.OwnerId = realOwner;   // normalize: receivers see the vehicle's real owner, not the sender
                Broadcast(MessageEnvelope.Create(MessageType.VehicleLockSet, MPConfig.PlayerId, p));
            });
        }

        // Host-local initiation (the HOST player boarding/exiting/locking — no round trip).
        // Call on the main thread.
        public static void HostBoardRequest(string vehicleId)
        {
            if (!_running) return;
            ResolveBoard(MPConfig.PlayerId, vehicleId);
        }

        public static void HostExit(string vehicleId)
        {
            if (!_running) return;
            PassengerSync.ApplyExit(MPConfig.PlayerId);
            Broadcast(MessageEnvelope.Create(MessageType.PassengerExit, MPConfig.PlayerId,
                new PassengerExitPayload { PlayerId = MPConfig.PlayerId, VehicleId = vehicleId }));
        }

        /// <summary>On a REFUSED lock set, unicast the vehicle's current true lock state back to the
        /// sender — their optimistic label corrects via HandleVehicleLockMsg → RefreshFor. Same
        /// message type and fields as an apply; receivers ignore OwnerId. MAIN THREAD.</summary>
        private static void EchoLockTruth(string senderPid, string vehicleId, string realOwner)
        {
            try
            {
                var peer = PeerForPlayer(senderPid);
                if (peer != null) Send(peer, MessageEnvelope.Create(MessageType.VehicleLockSet, MPConfig.PlayerId,
                    new VehicleLockPayload { OwnerId = realOwner, VehicleId = vehicleId, Locked = PassengerSync.IsLocked(vehicleId) }));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[LockBtn] refusal echo: {ex.Message}"); }
        }

        /// <summary>Host-local lock toggle. The host player may be the vehicle's OWNER or a
        /// currently-GRANTED key-holder (sitting in a borrowed ghost — the button passes the real
        /// id). Returns whether it applied, so the caller can label truthfully. MAIN THREAD.</summary>
        public static bool HostSetLock(string vehicleId, bool locked)
        {
            if (!_running || string.IsNullOrEmpty(vehicleId)) return false;
            string realOwner = PassengerSync.OwnerOf(vehicleId);
            if (string.IsNullOrEmpty(realOwner))
            {
                Plugin.Logger.LogInfo($"[LockBtn] host lock set on '{vehicleId}' refused — owner unknown here (fleet not synced yet?).");
                return false;
            }
            if (realOwner != MPConfig.PlayerId && !GrantSync.IsGranted(realOwner, MPConfig.PlayerId))
            {
                Plugin.Logger.LogInfo($"[LockBtn] host lock set on '{vehicleId}' refused — not the owner and no key.");
                return false;
            }
            PassengerSync.SetLock(vehicleId, locked);
            Broadcast(MessageEnvelope.Create(MessageType.VehicleLockSet, MPConfig.PlayerId,
                new VehicleLockPayload { OwnerId = realOwner, VehicleId = vehicleId, Locked = locked }));
            return true;
        }

        private static MPLink? PeerForPlayer(string pid)
        {
            if (!string.IsNullOrEmpty(pid))
                foreach (var peer in _clients.Keys)
                    if (_peerNames.TryGetValue(peer.Id, out var name) && name == pid) return peer;
            return null;
        }

        /// <summary>Online StableId → live PlayerId (every connected peer + the host itself).</summary>
        private static Dictionary<string, string> OnlinePidByStable()
        {
            var map = new Dictionary<string, string>();
            foreach (var pid in _peerNames.Values)
                if (StableIdByPlayer.TryGetValue(pid, out var st) && !string.IsNullOrEmpty(st)) map[st] = pid;
            if (!string.IsNullOrEmpty(MPConfig.StableId)) map[MPConfig.StableId] = MPConfig.PlayerId;   // host is online but not a peer
            return map;
        }

        /// <summary>HOST: rebuild the runtime (PlayerId-space) grant table from the durable StableId
        /// store for the CURRENT roster — only relationships where BOTH owner and grantee are online
        /// survive (which is all the enforcement checks ever need).</summary>
        private static void RebuildRuntimeGrants()
        {
            // Field 20260830-170317: this used to CLEAR and re-add only pairs resolvable to online
            // stable ids AT THIS INSTANT. During roster churn (a peer mid-handshake has a live name
            // but no learned stable id yet) a valid grant blinked off for one rebuild and back on
            // the next — and every blink flipped GrantorSig, whose consumer destroys and respawns
            // EVERY ghost vehicle (the bundle's host logged 'eXe.'↔'' seven times; the partner's
            // vans visibly vanished and reappeared). A grant may be dropped by a real DISCONNECT
            // or a store REVOCATION — never by a resolution blink. So: carry over prior runtime
            // grants whose parties are both still CONNECTED but not currently RESOLVABLE; fully
            // resolvable pairs always take the store's verdict (including revocation), and a
            // departed pid still drops immediately.
            var prev = GrantSync.SnapshotRuntime();
            GrantSync.ClearRuntime();
            var pidOf = OnlinePidByStable();
            int unresolvableStore = 0;
            foreach (var e in GrantSync.AllStoreEntries())
            {
                if (pidOf.TryGetValue(e.Owner, out var ownerPid) && pidOf.TryGetValue(e.Grantee, out var granteePid))
                    GrantSync.SetGrant(e.Kind, ownerPid, granteePid, true);
                else unresolvableStore++;
            }
            var connected = new HashSet<string>(_peerNames.Values) { MPConfig.PlayerId };
            var resolvable = new HashSet<string>(pidOf.Values);
            int carried = 0;
            foreach (var (kind, ownerPid, granteePid) in prev)
                if (connected.Contains(ownerPid) && connected.Contains(granteePid)
                    && (!resolvable.Contains(ownerPid) || !resolvable.Contains(granteePid)))
                { GrantSync.SetGrant(kind, ownerPid, granteePid, true); carried++; }
            // Ship-probes-with-fixes: if churn persists in a future bundle, these two counters name
            // which mapping blinked (store side unresolvable vs runtime carry) without a new round trip.
            if (carried > 0 || unresolvableStore > 0)
                Plugin.Logger.LogInfo($"[Server] grant rebuild: carried {carried} runtime grant(s) across a resolution gap; {unresolvableStore} store entr(ies) unresolvable this pass (offline or mid-handshake).");
        }

        /// <summary>HOST: build an owner's grantee list (incl. OFFLINE grantees) for the Permissions UI.</summary>
        private static PermissionOwnGrantsPayload BuildOwnGrants(string ownerStable)
        {
            var pay = new PermissionOwnGrantsPayload();
            if (string.IsNullOrEmpty(ownerStable)) return pay;
            var online = new HashSet<string>(OnlinePidByStable().Keys);
            foreach (var gs in GrantSync.AllGranteesOf(ownerStable))
                pay.Grantees.Add(new OwnGrantEntry {
                    Handle = gs, Name = GrantSync.NameOf(gs), Online = online.Contains(gs),
                    Kinds = GrantSync.StoreKindsFor(ownerStable, gs),
                });
            return pay;
        }

        /// <summary>Called from the scene-ready reset (MPCanvasUI): the scene wipe clears the RUNTIME grant
        /// table, so immediately rebuild it from the surviving store + roster and re-broadcast. Keeps
        /// enforcement correct regardless of scene-ready ordering across machines (no timing races).</summary>
        public static void RebuildGrantsAfterSceneReset()
        {
            if (!IsRunning) return;
            try { RefreshGrantsAndBroadcast(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] RebuildGrantsAfterSceneReset: {ex.Message}"); }
        }

        /// <summary>HOST: rebuild the runtime table, broadcast it to everyone, and refresh the host's
        /// OWN grantee list. Call after any grant change and on every roster change.</summary>
        private static void RefreshGrantsAndBroadcast()
        {
            RebuildRuntimeGrants();
            // Merger runtime BEFORE the building-access push below — RefreshBuildingAccess reads
            // IsGranted, which unions merger membership (slice 1).
            HostReconcileAbsence();   // P3-B: departures/joins/membership changes all land here first
            var merger = BuildMergerState();
            MergerSync.ApplyState(merger);
            Broadcast(MessageEnvelope.Create(MessageType.MergerState, "host", merger));
            Broadcast(MessageEnvelope.Create(MessageType.PermissionSnapshot, "host", GrantSync.BuildSnapshot()));
            GrantSync.SetMyGrantees(BuildOwnGrants(MPConfig.StableId).Grantees);
            RefreshBuildingAccess();            // housing: push each guest the buildings they may now enter
            VehicleManager.OnGrantsChanged();   // host is also a borrower: respawn its ghosts if its drivability changed
            MPSaveCoordinator.PersistGrantsNow();   // durably save grants the instant they change (a late grant was lost on load — manifest Grants=[], 2026-06-30)
        }

        /// <summary>HOST: the building addressKeys <paramref name="clientPid"/> may ENTER as a granted housing
        /// guest (AddressKeys) or WORK IN as a granted business helper (HelperAddressKeys). Clients can't
        /// compute this (no building→owner map), so the host pushes it. Round-32: each address is classified
        /// from the host's registry — a BUSINESS address is unlocked by the Business grant, everything else
        /// (homes, empty buildings, unknown) by the Housing grant.</summary>
        private static PermissionBuildingAccessPayload BuildBuildingAccessFor(string clientPid)
        {
            var pay = new PermissionBuildingAccessPayload();
            if (string.IsNullOrEmpty(clientPid)) return pay;

            var bizType = new Dictionary<string, string>();
            try
            {
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs != null)
                    foreach (var reg in regs)
                    {
                        try
                        {
                            string a = GameStateReader.AddressKey(reg);
                            if (!string.IsNullOrEmpty(a)) bizType[a] = reg.businessTypeName ?? "";
                        }
                        catch { }
                    }
            }
            catch { }
            bool IsBiz(string a) => bizType.TryGetValue(a, out var bt)
                && !string.IsNullOrEmpty(bt) && bt != "ba:businesstype_empty";

            var keys = new HashSet<string>(); var helper = new HashSet<string>(); var manage = new HashSet<string>();
            void Consider(string addr, string owner, bool operatorLedger)
            {
                if (string.IsNullOrEmpty(addr) || string.IsNullOrEmpty(owner)) return;
                string ownerPid = (owner == "host") ? MPConfig.PlayerId : owner;   // resolve the host sentinel to its real pid
                if (ownerPid == clientPid) return;                                  // the owner enters their own home natively
                if (IsBiz(addr))
                {   // helper access keys on who RUNS the business — the rental ledger only, never real-estate
                    // (a bought building can host an AI tenant's shop; helpers get no access there)
                    if (operatorLedger && GrantSync.IsGranted(GrantKind.Business, ownerPid, clientPid)) helper.Add(addr);
                    // Shared-shop MANAGEMENT (permission feature): direct grants only — merger membership never counts.
                    // Headquarters are never managed through permissions (user ruling 2026-08-21: the helper still
                    // sees it and may work inside it — helper access above — but its menus are not shared).
                    bool hq = bizType.TryGetValue(addr, out var bt2) && bt2 == "ba:businesstype_headquarters";
                    if (operatorLedger && !hq && GrantSync.IsGrantedDirect(GrantKind.Business, ownerPid, clientPid)) manage.Add(addr);
                }
                else if (GrantSync.IsGranted(GrantKind.Housing, ownerPid, clientPid)) keys.Add(addr);
            }
            foreach (var kv in BuildingOwners)           Consider(kv.Key, kv.Value, operatorLedger: true);
            foreach (var kv in BuildingRealEstateOwners) Consider(kv.Key, kv.Value, operatorLedger: false);
            pay.AddressKeys.AddRange(keys);
            pay.HelperAddressKeys.AddRange(helper);
            pay.SharedManageKeys.AddRange(manage);
            // Merger slice 3 repair: everything the operator ledger says belongs to someone else.
            foreach (var kv in BuildingOwners)
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                string ownerPid = (kv.Value == "host") ? MPConfig.PlayerId : kv.Value;
                if (ownerPid != clientPid) pay.OtherOwnedKeys.Add(kv.Key);
            }
            // 2026-09-05 colours: name the OWNER of every key this payload carries, so the client can tint each
            // shared building in that player's colour instead of one teal. Keys with no operator-ledger entry are
            // skipped — the client keeps the teal for those.
            void NameOwner(string k)
            {
                if (string.IsNullOrEmpty(k) || pay.Owners.ContainsKey(k)) return;
                if (!BuildingOwners.TryGetValue(k, out var ow) || string.IsNullOrEmpty(ow))
                {
                    // colours r2 (review r1 MINOR-14): a building that was BOUGHT but is not in the operator ledger
                    // still has an owner. BuildingRealEstateOwners is the same ConcurrentDictionary<string,string>
                    // holding the same values ("host" / a live playerId / a reserved stableId), so no mapping is needed.
                    if (!BuildingRealEstateOwners.TryGetValue(k, out ow) || string.IsNullOrEmpty(ow)) return;
                }
                pay.Owners[k] = (ow == "host") ? MPConfig.PlayerId : ow;
            }
            foreach (var k in pay.AddressKeys)       NameOwner(k);
            foreach (var k in pay.HelperAddressKeys) NameOwner(k);
            foreach (var k in pay.OtherOwnedKeys)    NameOwner(k);
            foreach (var k in pay.SharedManageKeys)  NameOwner(k);
            return pay;
        }

        private static void SendBuildingAccessTo(MPLink peer, string clientPid)
        {
            if (peer == null) return;
            try
            {
                var pay = BuildBuildingAccessFor(clientPid);
                Send(peer, MessageEnvelope.Create(MessageType.PermissionBuildingAccess, "host", pay));
                // Shared-shop slice 3: the benches of the owners whose shops this player may manage — deliberately AFTER
                // the access push and inside its try: if the push fails, no bench is shipped that the client could not scope.
                try { ReplaySharedPoolsTo(clientPid, pay.SharedManageKeys); } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendBuildingAccessTo: {ex.Message}"); }
        }

        /// <summary>HOST: push every connected client (and set the host's own) which buildings they may enter
        /// as a granted guest. Call after any grant or building-ownership change.</summary>
        public static void RefreshBuildingAccess()
        {
            if (!_running) return;
            try
            {
                foreach (var pid in new List<string>(_peerNames.Values))
                {
                    var pr = PeerForPlayer(pid);
                    if (pr != null) SendBuildingAccessTo(pr, pid);
                }
                var own = BuildBuildingAccessFor(MPConfig.PlayerId);
                PlayerColours.LearnOwners(own.Owners);   // colours r2 (review r1 MAJOR-1): the host learns the owner map too
                GrantSync.SetEnterableBuildings(own.AddressKeys);
                GrantSync.SetHelperBusinesses(own.HelperAddressKeys);
                GrantSync.SetSharedManage(own.SharedManageKeys);   // shared-shop management (permission feature)
                // The host as a permitted player: cached benches. MAIN THREAD ONLY — this applies the bench inline
                // (injects employee records, may refresh My Employees), and RefreshBuildingAccess is also reached from
                // the poll thread (HandleVacateRequest / HandleBuyRequest), so it is always marshalled.
                var ownManage = new List<string>(own.SharedManageKeys);
                try { GameStatePatcher.EnqueueOnMainThread(() => ReplaySharedPoolsTo(MPConfig.PlayerId, ownManage)); } catch { }
                GameStatePatcher.EnqueueOnMainThread(HousingMapCues.RefreshSharedPois);   // recolour the host's own shared-residence POIs (reciprocal sharing)
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] RefreshBuildingAccess: {ex.Message}"); }
        }

        private static void SendOwnGrantsTo(MPLink peer, string ownerStable)
        {
            if (peer == null) return;
            try { Send(peer, MessageEnvelope.Create(MessageType.PermissionOwnGrants, "host", BuildOwnGrants(ownerStable))); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendOwnGrantsTo: {ex.Message}"); }
        }

        private static void HandlePermissionGrantSet(string senderPid, MessageEnvelope env)
        {
            var p = env.GetPayload<PermissionGrantPayload>();
            if (p == null || !SenderIs(p.OwnerId, senderPid, MessageType.PermissionGrantSet)) return;   // only the real owner may grant
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                // Merger guard (2026-09-11): refuse a grant change between two members of the SAME company --
                // a stale or crafted client cannot flip a grant under a merger. No store write: M1 (2026-09-12)
                // already REMOVED the grants between co-members at merge time and nothing restores them at
                // unmerge, so this only refuses a NEW grant between co-members.
                if (!string.IsNullOrEmpty(p.GranteeId) && MergerSync.MergedRuntime(senderPid, p.GranteeId))
                {
                    Plugin.Logger.LogWarning($"[Merger] grant edit from '{senderPid}' for co-member '{p.GranteeId}' refused (merger overrides the three permissions)");
                    return;
                }
                // D16 r2 (re-check MINOR-1): the offline-by-handle row must not bypass the same rule for a
                // co-member who merely left this session (MemberPids keep them; the merger still stands).
                if (!string.IsNullOrEmpty(p.GranteeStable))
                {
                    string offPid = PidOfStable(p.GranteeStable);
                    if (offPid.Length > 0 && MergerSync.MergedRuntime(senderPid, offPid))
                    {
                        Plugin.Logger.LogWarning($"[Merger] offline grant edit from '{senderPid}' for co-member '{offPid}' refused (merger overrides the three permissions)");
                        return;
                    }
                }
                if (!StableIdByPlayer.TryGetValue(senderPid, out var ownerStable) || string.IsNullOrEmpty(ownerStable)) return;
                string granteeStable;
                if (!string.IsNullOrEmpty(p.GranteeStable)) granteeStable = p.GranteeStable;                  // offline revoke by handle
                else if (!string.IsNullOrEmpty(p.GranteeId)                                                   // online grant by pid
                         && StableIdByPlayer.TryGetValue(p.GranteeId, out var gs) && !string.IsNullOrEmpty(gs))
                { granteeStable = gs; GrantSync.NoteName(granteeStable, p.GranteeId); }
                else return;
                GrantSync.StoreSet(p.Kind, ownerStable, granteeStable, p.Granted);
                RefreshGrantsAndBroadcast();
                var ownerPeer = PeerForPlayer(senderPid);
                if (ownerPeer != null) SendOwnGrantsTo(ownerPeer, ownerStable);   // hand the owner back their list (with handles)
                // (Durable: written to the session manifest at the next coordinated save.)
            });
        }

        /// <summary>Host-local (the HOST is the owner): grant/revoke an ONLINE player by PlayerId.</summary>
        public static void HostSetGrant(GrantKind kind, string granteePid, bool granted)
        {
            if (!_running || string.IsNullOrEmpty(granteePid)) return;
            // Merger guard (user ruling 2026-09-11): the merger is a superset of the three permissions and
            // overrides them, so a grant between two members of the SAME company is refused here -- defence in
            // depth behind the hub's disabled toggles. M1 (2026-09-12): the grants between co-members were
            // REMOVED at merge time and nothing restores them at unmerge, so this refuses only a NEW grant.
            if (MergerSync.MergedRuntime(MPConfig.PlayerId, granteePid))
            {
                Plugin.Logger.LogWarning($"[Merger] grant change for co-member '{granteePid}' refused (merger overrides the three permissions)");
                return;
            }
            if (!StableIdByPlayer.TryGetValue(granteePid, out var gs) || string.IsNullOrEmpty(gs)) return;
            GrantSync.NoteName(gs, granteePid);
            GrantSync.StoreSet(kind, MPConfig.StableId, gs, granted);
            RefreshGrantsAndBroadcast();
            // (Durable: written to the session manifest at the next coordinated save.)
        }

        /// <summary>Host-local: revoke (or re-grant) a grantee by StableId handle — covers OFFLINE grantees.</summary>
        public static void HostSetGrantOffline(GrantKind kind, string granteeStable, bool granted)
        {
            if (!_running || string.IsNullOrEmpty(granteeStable)) return;
            // D16 r2: same rule as the online row - a co-member who left this session is still merged with me.
            // (M1 2026-09-12: their stored grants were removed at merge time; this refuses only a new one.)
            string offPid = PidOfStable(granteeStable);
            if (offPid.Length > 0 && MergerSync.MergedRuntime(MPConfig.PlayerId, offPid))
            {
                Plugin.Logger.LogWarning($"[Merger] offline grant edit for co-member '{offPid}' refused (merger overrides the three permissions)");
                return;
            }
            GrantSync.StoreSet(kind, MPConfig.StableId, granteeStable, granted);
            RefreshGrantsAndBroadcast();
            // (Durable: written to the session manifest at the next coordinated save.)
        }

        // ── Merger slice 1: form/dissolve arbitration (HOST, main thread) ────────
        // Consent flow: propose → the TARGET side accepts or declines → commit. Proposals PERSIST until
        // answered or withdrawn ("unpropose"); a withdraw/decline arms a COOLDOWN so nobody can spam a
        // player with proposal notifications (user, 2026-07-07). A session can hold several disjoint
        // merger groups; a player belongs to at most one. Any member may LEAVE at any time (a last pair
        // dissolves that group).
        // Phase 1-A (D4, user 2026-09-10): an offer goes to a whole COMPANY. The pending map is keyed by
        // a TARGET KEY — the target's groupId when they are in a company, else their pid — so ONE offer
        // reaches every member of that company and ANY member may answer for it. Accept UNIONS the two
        // sides (either may already be a company); there is no theoretical limit on members, and no group
        // outside the two in play is touched.
        /// <summary>Phase 1-A r4 (restructure): ONE pending offer. AskedPid is the pid the proposer
        /// CLICKED — the row their "Cancel offer" chip parks on even when the key names a company.
        /// (internal, not private: the TestDrive 'offers' verb prints From.)</summary>
        internal sealed class MergerOffer { public string From = ""; public string AskedPid = ""; }
        internal static readonly Dictionary<string, MergerOffer> _mergerPendingByTarget = new();   // target KEY → offer
        private static readonly Dictionary<string, long>   _mergerCooldown = new();          // "from|to" → next-allowed ms
        private const long MergerReproposeCooldownMs = 60_000;

        /// <summary>HOST: a StableId back to the PlayerId KNOWN THIS SESSION ("" when that member never connected this
        /// session). NOT an online test - StableIdByPlayer is never pruned on departure; use IsOnlinePid for that.</summary>
        private static string PidOfStable(string stable)
        {
            if (string.IsNullOrEmpty(stable)) return "";
            if (stable == MPConfig.StableId) return MPConfig.PlayerId;
            foreach (var kv in StableIdByPlayer) if (kv.Value == stable) return kv.Key;
            return "";
        }

        /// <summary>HOST, phase 1-A: the ONLINE members of ONE group, in join order. Empty for an
        /// unknown group — every caller stays keyed to the group ids in play (D4-4). r4: ONLINE now
        /// MEANS online — PidOfStable resolves through StableIdByPlayer, which keeps a departed
        /// member's handle for a rejoin, so every pid is checked against the live peer list.</summary>
        private static List<string> PidsOfGroup(string groupId)
        {
            var pids = new List<string>();
            if (string.IsNullOrEmpty(groupId) || !MergerSync.StoreGroups.ContainsKey(groupId)) return pids;
            foreach (var s in MergerSync.JoinOrderOfGroup(groupId))
            {
                string pid = PidOfStable(s);
                if (IsOnlinePid(pid)) pids.Add(pid);
            }
            return pids;
        }

        /// <summary>HOST, phase 1-A: everyone an offer keyed by <paramref name="targetKey"/> is addressed
        /// to — a whole company when the key names a group, else the single player the key names.</summary>
        private static List<string> PidsOfTargetKey(string targetKey)
        {
            if (MergerSync.StoreGroups.ContainsKey(targetKey)) return PidsOfGroup(targetKey);
            var one = new List<string>();
            if (IsOnlinePid(targetKey)) one.Add(targetKey);   // r4: a pid key whose player left addresses nobody
            return one;
        }

        /// <summary>HOST, phase 1-A r2: liveness by CONNECTION. StableIdByPlayer is never pruned on a
        /// departure (a rejoiner keeps their handle), so a stable lookup is NOT an online test.</summary>
        internal static bool IsOnlinePid(string pid)   // internal since POPUPS-1b: the `grant` rig lever asks it
            => !string.IsNullOrEmpty(pid) && (pid == MPConfig.PlayerId || PeerForPlayer(pid) != null);

        /// <summary>HOST, phase 1-A r4: retire ONE pending entry. Nobody's ROW is cleared here — every
        /// machine derives its incoming row and its Cancel chip from the offer table in the state
        /// broadcast (A3/A4), so there is no "withdrawn" relay any more. The proposer still hears that
        /// their offer died (the existing "declined" toast), and only if they are online. No cooldown —
        /// nobody declined. The CALLER broadcasts once, after the whole prune.</summary>
        private static void DropMergerOffer(string key, string reason)
        {
            if (string.IsNullOrEmpty(key) || !_mergerPendingByTarget.TryGetValue(key, out var off)) return;
            string fromPid = off?.From ?? "";
            _mergerPendingByTarget.Remove(key);
            Plugin.Logger.LogInfo($"[Merger] offer from '{fromPid}' to '{key}' dropped: {reason}.");
            if (!IsOnlinePid(fromPid)) return;
            if (fromPid == MPConfig.PlayerId) PassengerHud.Toast("Merger proposal declined.");
            else SendToPid(fromPid, MessageEnvelope.Create(MessageType.MergerRequest, "host",
                 new MergerRequestPayload { Action = "declined", FromPid = off?.AskedPid ?? "" }));
        }

        /// <summary>HOST, phase 1-A r4 — THE offer-table validator, and the only thing that retires an
        /// entry. Three rounds of per-case patching kept missing cases (a departed proposer, a company
        /// that dissolved under its own offer, a proposer who joined the company he had proposed to), so
        /// the rule lives in ONE place and every mutation of the store or the table ends here. An entry
        /// survives only if ALL of: its proposer is ONLINE; its key still resolves to an online player or
        /// to a live company with at least one ONLINE member; and its proposer is NOT already inside the
        /// company the key names (nothing left to merge). Returns true when something was removed —
        /// the caller then rebroadcasts the state.</summary>
        private static bool PruneOffers(string reason)
        {
            if (_mergerPendingByTarget.Count == 0) return false;
            var dead = new List<string>();
            foreach (var kv in _mergerPendingByTarget)
            {
                string key = kv.Key, from = kv.Value?.From ?? "";
                bool keyIsGroup = MergerSync.StoreGroups.ContainsKey(key);
                if (!IsOnlinePid(from)) { dead.Add(key); continue; }                       // (i) proposer gone
                if (!(keyIsGroup ? PidsOfGroup(key).Count > 0 : IsOnlinePid(key))) { dead.Add(key); continue; }   // (ii) nobody can answer
                string gFrom = MergerSync.GroupOfStable(StableOfPid(from));
                bool inside = keyIsGroup
                            ? gFrom == key
                            : (gFrom != "" && gFrom == MergerSync.GroupOfStable(StableOfPid(key)));
                if (inside) dead.Add(key);                                                 // (iii) proposer is already in there
            }
            foreach (var k in dead) DropMergerOffer(k, reason);
            // (iv) r6 (review r4 #1): an offer addressed to a PLAYER who has since joined a company is an offer to
            // that company (D4-1) - re-key it so every member can answer and only ONE offer per side exists; when
            // that company already holds an offer, this one goes (the company is "busy").
            var rekey = new List<string>();
            foreach (var kv in _mergerPendingByTarget)
                if (!MergerSync.StoreGroups.ContainsKey(kv.Key) && MergerSync.GroupOfStable(StableOfPid(kv.Key)) != "") rekey.Add(kv.Key);
            foreach (var k in rekey)
            {
                string g = MergerSync.GroupOfStable(StableOfPid(k));
                var off = _mergerPendingByTarget[k];
                if (string.IsNullOrEmpty(g) || off == null) continue;
                if (_mergerPendingByTarget.ContainsKey(g)) { DropMergerOffer(k, "their company already holds an offer"); dead.Add(k); continue; }
                _mergerPendingByTarget.Remove(k);
                _mergerPendingByTarget[g] = off;
                dead.Add(k);   // the table changed - the caller broadcasts
                Plugin.Logger.LogInfo($"[Merger] offer from '{off.From}' to '{k}' re-keyed to company '{g}' ({reason}).");
            }
            return dead.Count > 0;
        }

        /// <summary>HOST, phase 1-A: the pending offer this actor may answer — their company's first,
        /// then one addressed to them personally. "" when there is none.</summary>
        private static string PendingKeyFor(string actorPid)
        {
            string g = MergerSync.GroupOfStable(StableOfPid(actorPid));
            if (!string.IsNullOrEmpty(g) && _mergerPendingByTarget.ContainsKey(g)) return g;
            return _mergerPendingByTarget.ContainsKey(actorPid) ? actorPid : "";
        }

        public static void HostMergerAction(string action, string targetPid, string actorPid)
        {
            if (!_running || string.IsNullOrEmpty(actorPid)) return;
            long now = TickMs64;   // monotonic ms (net48 has no Environment.TickCount64)

            switch (action)
            {
                case "propose":
                {
                    if (string.IsNullOrEmpty(targetPid) || targetPid == actorPid) return;
                    if (!IsOnlinePid(targetPid)) return;                             // target must be CONNECTED (a stable handle outlives a departure)
                    string gActor = MergerSync.GroupOfStable(StableOfPid(actorPid));
                    string gTgt   = MergerSync.GroupOfStable(StableOfPid(targetPid));
                    if (gActor != "" && gActor == gTgt) return;                      // D4-1: already the SAME company
                    string tkey = gTgt != "" ? gTgt : targetPid;                     // TARGET KEY: their company, else them
                    if (_mergerPendingByTarget.TryGetValue(tkey, out var held) && held?.From != actorPid)
                    {   // r2 (review #2; wording approved 2026-09-11): SOMEONE ELSE's offer is pending on that side - say why nothing happens
                        // (r5: my OWN pending offer to the same side is not "busy" - it is re-sent below as a reminder)
                        var busy = new MergerRequestPayload { Action = "busy", FromPid = targetPid };
                        if (actorPid == MPConfig.PlayerId) PassengerHud.Toast("They already have an offer pending.");   // r4: toast only - no chip to clear
                        else SendToPid(actorPid, MessageEnvelope.Create(MessageType.MergerRequest, "host", busy));
                        return;
                    }
                    if (_mergerCooldown.TryGetValue(actorPid + "|" + tkey, out var next) && now < next)
                    {   // anti-spam: a withdrawn/declined offer can't be re-sent immediately
                        var cool = new MergerRequestPayload { Action = "cooldown", FromPid = targetPid };
                        if (actorPid == MPConfig.PlayerId) PassengerHud.Toast("Wait a minute before proposing to them again.");   // r4: toast only - the chip follows the broadcast table, not this call
                        else SendToPid(actorPid, MessageEnvelope.Create(MessageType.MergerRequest, "host", cool));
                        return;
                    }
                    // r5 (user decision 2026-09-11): a new offer REPLACES the proposer's existing one - and re-offering the
                    // SAME side re-sends it as a reminder. The old key takes the withdraw cooldown, so a reminder to one
                    // side is possible at most once a minute; the old addressees' rows clear through the state broadcast.
                    string prior = "";
                    foreach (var kv in _mergerPendingByTarget) if (kv.Value?.From == actorPid) { prior = kv.Key; break; }
                    if (prior != "")
                    {
                        _mergerPendingByTarget.Remove(prior);
                        _mergerCooldown[actorPid + "|" + prior] = now + MergerReproposeCooldownMs;
                        Plugin.Logger.LogInfo($"[Merger] '{actorPid}' withdrew the proposal to '{prior}' ({(prior == tkey ? "re-sent as a reminder" : "replaced by a new offer")}).");
                    }
                    _mergerPendingByTarget[tkey] = new MergerOffer { From = actorPid, AskedPid = targetPid };
                    if (gTgt != "")
                        Plugin.Logger.LogInfo($"[Merger] '{actorPid}' proposes a merger to company '{gTgt}' (asked '{targetPid}'); every online member may answer.");
                    else
                        Plugin.Logger.LogInfo($"[Merger] '{actorPid}' proposes a merger to '{targetPid}'.");
                    var relay = new MergerRequestPayload { Action = "proposal", FromPid = actorPid };
                    // D4-1: the NOTIFICATION reaches every online member of the target side; the ROW each of
                    // them sees is derived from the offer table in the state broadcast below (r4).
                    foreach (var pid in PidsOfTargetKey(tkey))
                    {
                        if (pid == MPConfig.PlayerId) PassengerHud.Toast($"{actorPid} proposes a company merger — see Permissions.");
                        else SendToPid(pid, MessageEnvelope.Create(MessageType.MergerRequest, "host", relay));
                    }
                    RebroadcastMergerState(true);
                    break;
                }
                case "unpropose":
                {
                    string tgt = "";
                    foreach (var kv in _mergerPendingByTarget) if (kv.Value?.From == actorPid) { tgt = kv.Key; break; }
                    if (tgt == "") return;
                    _mergerPendingByTarget.Remove(tgt);
                    _mergerCooldown[actorPid + "|" + tgt] = now + MergerReproposeCooldownMs;
                    Plugin.Logger.LogInfo($"[Merger] '{actorPid}' withdrew the proposal to '{tgt}'.");
                    // r4: no relay — every addressee's incoming row clears because the entry is gone from
                    // the broadcast table, and a withdraw stays silent (it must not be a notification channel).
                    PruneOffers("stale at withdraw");
                    RebroadcastMergerState(true);
                    break;
                }
                case "accept":
                {
                    // D4-2: ANY member of the target company may answer — the offer is keyed by their group.
                    string akey = PendingKeyFor(actorPid);
                    if (akey == "" || !_mergerPendingByTarget.TryGetValue(akey, out var aoff)) return;
                    string fromPid = aoff?.From ?? "";
                    string a = StableOfPid(fromPid), b = StableOfPid(actorPid);
                    if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;   // someone left mid-consent (r6: table untouched - the departure prune retires the entry with a broadcast)
                    _mergerPendingByTarget.Remove(akey);
                    string gA = MergerSync.GroupOfStable(a), gB = MergerSync.GroupOfStable(b);
                    // Phase 1-A (2026-09-10, D4-3): the phase-0 'target already in a company' refusal is REPLACED by
                    // the union below — only the degenerate case survives: the two sides became the SAME company
                    // while the offer was pending, so there is nothing left to merge.
                    if (gA != "" && gA == gB)
                    {
                        Plugin.Logger.LogWarning($"[Merger] accept by '{actorPid}' of '{fromPid}' REFUSED - they are already in the same company (joined while the offer was pending).");
                        _mergerCooldown[fromPid + "|" + akey] = now + MergerReproposeCooldownMs;   // r3 (re-review #4): same anti-spam as a decline
                        // r2 (review #6): the proposer learns the offer died - same toast as a decline. r4: the
                        // addressees' rows and the proposer's chip follow the broadcast table below, not a relay.
                        if (fromPid == MPConfig.PlayerId) PassengerHud.Toast("Merger proposal declined.");
                        else if (IsOnlinePid(fromPid)) SendToPid(fromPid, MessageEnvelope.Create(MessageType.MergerRequest, "host",
                                 new MergerRequestPayload { Action = "declined", FromPid = actorPid }));
                        PruneOffers("unanswerable after a same-company accept");
                        RebroadcastMergerState(true);
                        return;
                    }
                    // Durable roster names: NOT the pid (that is what made MemberNames read as ids).
                    GrantSync.NoteName(a, DisplayNameFor(fromPid)); GrantSync.NoteName(b, DisplayNameFor(actorPid));
                    // M1 (user ruling 2026-09-12): the merger REPLACES the three permissions, it is not added to
                    // them - so every STORED grant between the future co-members is removed HERE, before the
                    // membership commit below, and nothing brings it back at unmerge. The future member set is
                    // the members of both groups plus the two actors (StableId space, MergerSync.StoreGroups).
                    // The branch tails below both call RefreshGrantsAndBroadcast() after the commit, which is
                    // what republishes the runtime table these removals just changed; the next coordinated save
                    // then persists their absence (MPSaveCoordinator.cs:3641-3643 builds the manifest from
                    // GrantSync.AllStoreEntries(), and the restore at :386-395 only re-adds what the file holds).
                    var futureMembers = new HashSet<string> { a, b };
                    if (gA != "" && MergerSync.StoreGroups.TryGetValue(gA, out var mSetA)) foreach (var s in mSetA) futureMembers.Add(s);
                    if (gB != "" && MergerSync.StoreGroups.TryGetValue(gB, out var mSetB)) foreach (var s in mSetB) futureMembers.Add(s);
                    int grantsRemoved = 0;
                    foreach (var ownerStable in futureMembers)
                        foreach (var granteeStable in futureMembers)
                        {
                            if (ownerStable == granteeStable) continue;
                            foreach (var gk in GrantSync.StoreKindsFor(ownerStable, granteeStable))
                            { GrantSync.StoreSet(gk, ownerStable, granteeStable, false); grantsRemoved++; }
                        }
                    if (grantsRemoved > 0)
                        Plugin.Logger.LogInfo($"[Merger] permissions removed between the new co-members: {grantsRemoved} grant(s) (the merger replaces them; nothing comes back at unmerge).");
                    // D4-3 UNION, one host call, no observer can see a member in two groups: neither side in a
                    // company mints a pair; one side in a company takes the other in; BOTH in companies merges
                    // them into the OLDER group (its id, its founder, its join order first) and pools the wallets.
                    string group;
                    if (gA == "" && gB == "")
                    {
                        group = MergerSync.StoreAdd("", a);
                        MergerSync.StoreAdd(group, b);
                        Plugin.Logger.LogInfo($"[Merger] FORMED group '{group}': '{fromPid}' + '{actorPid}'.");
                    }
                    else if (gB == "")
                    {
                        group = MergerSync.StoreAdd(gA, b);
                        Plugin.Logger.LogInfo($"[Merger] GROWN group '{group}': '{actorPid}' joined '{fromPid}'.");
                    }
                    else if (gA == "")
                    {
                        group = MergerSync.StoreAdd(gB, a);   // the PROPOSER joins the target's company
                        Plugin.Logger.LogInfo($"[Merger] GROWN group '{group}': '{fromPid}' joined '{actorPid}'.");
                    }
                    else
                    {
                        long sA = MergerSync.SeqOfGroup(gA), sB = MergerSync.SeqOfGroup(gB);
                        group = (sA != 0 && (sB == 0 || sA <= sB)) ? gA : gB;   // the OLDER company keeps its id
                        string other = group == gA ? gB : gA;
                        // r4: a third party's offer keyed on the ABSORBED id, and any offer whose proposer is now
                        // inside the survivor, are retired by PruneOffers below — AFTER the store change, because
                        // the validator asks the store what is still answerable rather than guessing per case.
                        MergerSync.StoreUnion(group, other);
                        MarkMergerStateAuthoritative();   // X3: a UNION is this process deciding the merger state
                        // r2 (review #3): cooldowns keyed on the absorbed id follow it to the survivor.
                        var rekey = new List<string>();
                        foreach (var ck in _mergerCooldown.Keys) if (ck.EndsWith("|" + other, StringComparison.Ordinal)) rekey.Add(ck);
                        foreach (var ck in rekey)
                        {
                            long until = _mergerCooldown[ck]; _mergerCooldown.Remove(ck);
                            string nk = ck.Substring(0, ck.Length - other.Length) + group;
                            if (!_mergerCooldown.TryGetValue(nk, out var have) || have < until) _mergerCooldown[nk] = until;
                        }
                        // Wallets merge HOST-SIDE so the absorbed members' shared cash is not stranded on a
                        // group id that no longer exists (no contribution ledger by design — "this is ours").
                        if (_walletBalance.TryGetValue(other, out var ob))
                        {
                            _walletBalance.TryGetValue(group, out var kb);
                            _walletBalance[group] = kb + ob;
                        }
                        _walletBalance.Remove(other);
                        if (_walletContributed.TryGetValue(other, out var oc))
                        {
                            if (!_walletContributed.TryGetValue(group, out var kc)) { kc = new HashSet<string>(); _walletContributed[group] = kc; }
                            foreach (var s in oc) kc.Add(s);
                        }
                        _walletContributed.Remove(other);
                        int unionCount = MergerSync.StoreGroups.TryGetValue(group, out var uset) ? uset.Count : 0;
                        Plugin.Logger.LogInfo($"[Merger] UNION group '{other}' -> '{group}': members now {unionCount}.");
                        PruneOffers("unanswerable after a merger accept");
                        RefreshGrantsAndBroadcast();   // carries the pruned offer table with the new membership
                        // AFTER the state broadcast: MergerWallet.ApplyState drops any balance whose GroupId is
                        // not the receiver's CURRENT group, and the absorbed members only just learned theirs.
                        BroadcastWalletGroup(group);
                        break;
                    }
                    MarkMergerStateAuthoritative();   // X3: a company was FORMED or GROWN here — the live store is now the truth
                    PruneOffers("unanswerable after a merger accept");
                    RefreshGrantsAndBroadcast();   // carries the pruned offer table with the new membership
                    break;
                }
                case "decline":
                {
                    // D4-2: any member of the target company may decline for it.
                    string dkey = PendingKeyFor(actorPid);
                    if (dkey == "" || !_mergerPendingByTarget.TryGetValue(dkey, out var doff)) return;
                    string fromPid = doff?.From ?? "";
                    _mergerPendingByTarget.Remove(dkey);
                    _mergerCooldown[fromPid + "|" + dkey] = now + MergerReproposeCooldownMs;
                    Plugin.Logger.LogInfo($"[Merger] '{actorPid}' declined '{fromPid}'.");
                    var relay = new MergerRequestPayload { Action = "declined", FromPid = actorPid };
                    if (fromPid == MPConfig.PlayerId) PassengerHud.Toast("Merger proposal declined.");
                    else if (IsOnlinePid(fromPid)) SendToPid(fromPid, MessageEnvelope.Create(MessageType.MergerRequest, "host", relay));
                    // r4: the decliner AND their co-members lose the incoming row because the entry is gone
                    // from the broadcast table below — no silent "withdrawn" relay, no host-local writes.
                    PruneOffers("stale at decline");
                    RebroadcastMergerState(true);
                    break;
                }
                case "leave":
                {
                    string s = StableOfPid(actorPid);
                    string g0 = MergerSync.GroupOfStable(s);
                    if (string.IsNullOrEmpty(s) || g0 == "") return;

                    // Slice 4 — settle the shared wallet BEFORE membership changes. Equal split: the
                    // leaver takes balance / memberCount; a dissolving pair's REMAINING member takes
                    // the rest as their personal wallet (combined entity — no contribution tracking
                    // by design; "this is ours" has no ledgers of whose money it was).
                    if (_walletBalance.TryGetValue(g0, out var bal))
                    {
                        int members = MergerSync.StoreGroups.TryGetValue(g0, out var set) ? set.Count : 0;
                        if (members > 0)
                        {
                            float share = bal / members;
                            // FOLD b X2 (r1 F2): EVERY payout decided here is written into CashByStableId at once,
                            // online or offline. Only the OFFLINE member's figure used to be recorded, so an ONLINE
                            // member kept the full-pot MIRROR as their "last known cash" until the 3 s resync
                            // (MPCanvasUI.cs:4359) — and a coordinated save or MergeSlot upload inside that window
                            // stamped the WHOLE balance into their slot, handing each of them the full pot on the
                            // next load. The leaver goes first: they are leaving the group, so from this instant
                            // CashByStableId is the only thing that answers for them.
                            CashByStableId[s] = share;
                            var payout = new MergerWalletStatePayload { GroupId = "", Balance = share };
                            if (actorPid == MPConfig.PlayerId) MergerWallet.ApplyState(payout);
                            else SendToPid(actorPid, MessageEnvelope.Create(MessageType.MergerWalletState, "host", payout));
                            Plugin.Logger.LogInfo($"[EconProbe] wallet SPLIT '{g0}': '{actorPid}' leaves with ${share:N0} of ${bal:N0} ({members} member(s)).");
                            if (members <= 2)
                            {
                                // Group dissolves — the other member gets the remainder as personal cash.
                                string other = "";
                                foreach (var mem in set) if (mem != s) { other = mem; break; }
                                string otherPid = "";
                                if (other == MPConfig.StableId) otherPid = MPConfig.PlayerId;
                                else foreach (var kv in StableIdByPlayer) if (kv.Value == other) { otherPid = kv.Key; break; }
                                var rest = new MergerWalletStatePayload { GroupId = "", Balance = bal - share };
                                if (otherPid == MPConfig.PlayerId) MergerWallet.ApplyState(rest);
                                else if (otherPid != "") SendToPid(otherPid, MessageEnvelope.Create(MessageType.MergerWalletState, "host", rest));
                                // X2: the remaining member's figure regardless of who they are — the company is gone,
                                // so CashByStableId is their only record, exactly as for the leaver above.
                                if (other != "") CashByStableId[other] = bal - share;
                                Plugin.Logger.LogInfo($"[EconProbe] wallet DISSOLVE '{g0}': remaining member gets ${bal - share:N0}.");
                                _walletBalance.Remove(g0);
                                _walletContributed.Remove(g0);
                            }
                            else
                            {
                                _walletBalance[g0] = bal - share;
                                if (_walletContributed.TryGetValue(g0, out var cset)) cset.Remove(s);
                                // X2: the company SURVIVES, so the members who stay are re-shared over the reduced
                                // pot and count. Their live figure is written too, so CashByStableId never holds the
                                // pre-leave full pot for anybody. The remainder holder is the founder, or — when the
                                // founder is the one leaving — the next member in join order, which is exactly what
                                // MergerSync.StoreRemove installs a few lines below.
                                var stay = new List<string>();
                                foreach (var mem in MergerSync.JoinOrderOfGroup(g0)) if (mem != s) stay.Add(mem);
                                if (stay.Count > 0)
                                {
                                    string keeper = MergerSync.FounderOfGroup(g0);
                                    if (keeper == s || !stay.Contains(keeper)) keeper = stay[0];
                                    foreach (var mem in stay)
                                        CashByStableId[mem] = MPSaveCoordinator.EqualShare(bal - share, stay.Count, mem == keeper);
                                }
                                BroadcastWalletGroup(g0);
                            }
                        }
                    }

                    // PHASE 5 / P7 (2026-09-12), narrowed by r2 (J7): a routed PRESS waiting on a partner must
                    // never run after the company that authorised it changed. The peer-departure path already
                    // forgets them (:1783); "leave" never did. WHOSE presses go depends on what the leave
                    // does to the company: a 3+ company SURVIVES it, so only the LEAVER loses their pending
                    // presses and the members who stay keep both their company and theirs; a leave that takes
                    // the member count below two DISSOLVES the company, and then every member loses it.
                    // Collected BEFORE the store changes, forgotten after it.
                    var exPids = new List<string>();
                    bool dissolves = !MergerSync.StoreGroups.TryGetValue(g0, out var lset) || lset.Count <= 2;
                    if (!dissolves) exPids.Add(actorPid);
                    else if (lset != null)
                        foreach (var mem in lset)
                        {
                            string mpid = mem == MPConfig.StableId ? MPConfig.PlayerId : "";
                            if (mpid.Length == 0)
                                foreach (var kv in StableIdByPlayer) if (kv.Value == mem) { mpid = kv.Key; break; }
                            if (mpid.Length > 0 && !exPids.Contains(mpid)) exPids.Add(mpid);
                        }
                    // FOLD c C3 (r1 MINOR-3): the press list above must be pids (a press is held per pid), and
                    // the stable -> pid reverse lookup only answers for a pid this session has seen, taking the
                    // FIRST match - so a member who is offline, or who rejoined under a second pid, can be
                    // missed. The merger store keeps STABLE ids, so the TRANSFER step below matches on those
                    // instead: every stable in the group when the company dissolves, the leaver's alone when it
                    // survives. Collected BEFORE StoreRemove, like exPids (lset is the store's own set).
                    var exStables = new HashSet<string>(StringComparer.Ordinal);
                    if (!dissolves) exStables.Add(s);
                    else if (lset != null)
                        foreach (var mem in lset) if (!string.IsNullOrEmpty(mem)) exStables.Add(mem);

                    MergerSync.StoreRemove(s);
                    MarkMergerStateAuthoritative();   // X3: a leave — and the DISSOLVE it may be — is this process deciding the merger state, so the empty store it can leave behind must be persisted, not kept off disk
                    foreach (var xp in exPids)
                    {
                        GameStatePatcher.EnqueueOnMainThread(() => HostForgetPressesOf(xp));
                        Plugin.Logger.LogInfo($"[Merger] presses forgotten for '{xp}' — their company changed on leave ({(dissolves ? "the company dissolved" : "they left it")}).");
                    }
                    HostForgetCandidatesOf(actorPid);   // phase 4b (people) r2 (MINOR-9): their pool rows and claims leave with them
                    Plugin.Logger.LogInfo($"[Merger] '{actorPid}' left their merger group.");

                    // DISSOLVE step 5 (2026-09-12, user ruling) - HOST ONLY. An OPEN employee transfer the
                    // host is holding was authorised by a company that has just changed, so it must not land
                    // on a machine that is no longer a co-member. Value-based and idempotent: a transfer
                    // nobody has released yet has moved NOTHING and is simply cancelled (the entry is kept a
                    // game day exactly as the deadline path keeps it, so a release still in the air finds it);
                    // one the host is already holding goes HOME down the existing "return" leg. A transfer
                    // that is already going back is left alone, and an adoption that already COMPLETED is a
                    // real record on its new machine and is correct to keep - nothing here touches it.
                    // Fold c: the sides are matched on STABLE ids, so an OFFLINE member's held transfers are
                    // swept too. "Home" for an offline source is what RelayReturn already does - it hands the
                    // record to whoever runs the from-address as a stand-in, and with nobody there it logs
                    // "the host keeps '<employee>' until one of them is back" (:7687) and keeps holding it,
                    // re-offered once a game hour by the transfers tick. There is no queue for an absent pid.
                    int xBack = 0, xCancelled = 0;
                    foreach (var xt in new List<HostTransfer>(_transfers.Values))
                    {
                        if (xt == null) continue;
                        if (!exStables.Contains(StableOfPid(xt.SourcePid))
                            && !exStables.Contains(StableOfPid(xt.DestPid))) continue;
                        if (xt.Stage == "requested")
                        {
                            xt.Stage = "cancelled"; xt.Day = GameDayNow(); xt.Hour = GameHourNow();
                            xCancelled++;
                        }
                        else if (xt.Stage == "adopting")
                        {
                            xt.Day = GameDayNow(); xt.Hour = GameHourNow();
                            RelayReturn(xt);
                            xBack++;
                        }
                    }
                    if (xBack > 0 || xCancelled > 0)
                        Plugin.Logger.LogInfo($"[Dissolve] transfers at leave: {xBack} held transfer(s) sent back to their source and "
                                            + $"{xCancelled} un-released one(s) cancelled - the company that authorised them changed.");

                    // DISSOLVE step 6 (D3) - HOST ONLY. An ABSENCE MARK is one member standing in for another
                    // member's buildings while they are away; the moment that company changes the stand-in is
                    // not a co-member of that owner any more, so the HAND-BACK runs NOW instead of waiting up
                    // to 10 s for the reconcile's dead sweep (:835) to notice. HostDropMark IS the hand-back:
                    // it drops the mark and sends the existing MergerHandover "Drop" leg, which is what makes
                    // the stand-in lift the installed paperwork, give up the veil exception and hand the
                    // promoted staff back (MergerAbsence.ApplyHandover -> UndoLocal) - no new message type.
                    // Nothing has to be UPLOADED here: the stand-in's publishes were filed under the OWNER's
                    // stable at every publish, so the host copy already is the owner's state, and the owner's
                    // own return leg still runs when they come back. An OFFLINE owner is reached through their
                    // simulator (their own pid is not in StableIdByPlayer, their stand-in's is).
                    var deadMarks = new List<string>();
                    foreach (var mk in MergerAbsence.Marks)
                    {
                        var mv = mk.Value;
                        if (mv == null) continue;
                        if (exPids.Contains(mv.OwnerPid) || exPids.Contains(mv.SimulatorPid)) deadMarks.Add(mk.Key);
                    }
                    foreach (var st in deadMarks)
                        MergerAbsence.HostDropMark(st, "the company that made this stand-in was cancelled");
                    if (deadMarks.Count > 0)
                        Plugin.Logger.LogInfo($"[Dissolve] absence at leave: {deadMarks.Count} stand-in mark(s) handed back - "
                                            + "the company that made them was cancelled.");
                    // r4: a dissolving company's offer (and one the leaver had out that nobody can answer any
                    // more) is retired by the validator AFTER the store change — a 3+ company keeps both.
                    PruneOffers("unanswerable after a member left the company");
                    RefreshGrantsAndBroadcast();
                    break;
                }
            }
        }

        private static string StableOfPid(string pid)
            => pid == MPConfig.PlayerId ? MPConfig.StableId
             : StableIdByPlayer.TryGetValue(pid, out var s) ? (s ?? "") : "";

        internal static void SendToPid(string pid, MessageEnvelope env)   // internal since the AI-staff slice (RivalStaffSync answers requesters directly)
        {
            foreach (var peer in _clients.Keys)
                if (_peerNames.TryGetValue(peer.Id, out var p) && p == pid) { Send(peer, env); return; }
        }

        /// <summary>Relay a grant-gated business edit to the owning client (merger slice 3).</summary>
        public static void SendBusinessEditTo(string ownerPid, BusinessEditPayload p)
            => SendToPid(ownerPid, MessageEnvelope.Create(MessageType.BusinessEditRequest, "host", p));

        /// <summary>HOST (main thread), slice 5: gate a routed employee/schedule op — the sender must
        /// own the shop or hold Business access to its owner (the merger unions into that grant) —
        /// then apply locally (host owns it) or relay to the owner's machine.</summary>
        public static void HostRouteEmployeeEdit(EmployeeEditPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(senderPid)) return;
                // Phase 4b (people) P2 r2 (T1): the transfer legs are the HOST's own conversation - it asks
                // for the release, holds the record, relays the adopt and hands it back. They are validated
                // in HostRouteTransfer, not by the per-address grant gate below, and they are read BEFORE
                // the address check: a move out of the asker's OWN save carries no from-address at all.
                if (p.Action == "transfer-request" || p.Action == "released" || p.Action == "adopted"
                 || p.Action == "transfer-refused" || p.Action == "returned" || p.Action == "dropped"
                 || p.Action == "release-refused")
                { HostRouteTransfer(p, senderPid); return; }
                // CROSS-HR-2 T2: a training leg is not an address edit. It is routed to the machine that holds
                // the REAL record, which is normally the SENDER's own shop's owner-side counterpart - the gate
                // below would look the address up, find the sender runs it, and drop the leg as a mis-route.
                // CROSS-HR-3 A2: a TAG leg is the same shape and takes the same route - one router, two actions.
                if (p.Action == "hrtrain" || p.Action == "hrtag") { HostRouteHrTrain(p, senderPid); return; }
                // CROSS-HR-3b B2: the ANSWER to a tag the owner could not write is addressed by PID as well -
                // back to the plan's RUNNER, named by OwnerPid - so it is read before the address gate too.
                if (p.Action == "hrtag-refused") { HostRouteHrTagRefused(p, senderPid); return; }
                if (string.IsNullOrEmpty(p.AddressKey)) return;
                if (!BuildingOwners.TryGetValue(p.AddressKey, out var owner) || string.IsNullOrEmpty(owner))
                { Plugin.Logger.LogWarning($"[MergerStaff] employee edit for unowned '{p.AddressKey}' — dropped."); return; }
                string ownerPid = owner == "host" ? MPConfig.PlayerId : owner;
                if (ownerPid != senderPid && !GrantSync.IsGranted(GrantKind.Business, ownerPid, senderPid))
                { Plugin.Logger.LogWarning($"[MergerStaff] employee edit by '{senderPid}' on '{p.AddressKey}' (owner '{ownerPid}') — no access, dropped."); return; }
                // J2 (user ruling 2026-09-12): helpers never fire, members may. GrantSync.IsGranted UNIONS a
                // direct Business grant with merger membership, so a permissions helper would otherwise pass the
                // gate above for every action - including "fire" (MergerEmployeeSync.cs:43). Firing is the one op
                // reserved to the owner himself or a co-member of the same company; assign/adopt/transfer stay on
                // the union, because those are exactly what helpers and members share.
                if (p.Action == "fire" && ownerPid != senderPid && !MergerSync.MergedRuntime(ownerPid, senderPid))
                { Plugin.Logger.LogWarning($"[MergerStaff] fire by '{senderPid}' on '{p.AddressKey}' (owner '{ownerPid}') — a business helper may not fire the owner's staff; only a company member may. Dropped."); return; }
                // W3-0 r1 (F6): the fifth write route joins the other four — an offline owner's edit goes to
                // the machine simulating them (MergerEmployeeSync.ApplyOnOwner already accepts SimulatesHere),
                // and with nobody running the address RouteTargetFor logs the refusal and we drop it.
                string etarget = RouteTargetFor(p.AddressKey, ownerPid);
                if (etarget.Length == 0) return;                       // RouteTargetFor logged why
                if (etarget == senderPid)
                { Plugin.Logger.LogWarning($"[MergerStaff] employee edit by '{senderPid}' on '{p.AddressKey}' — that machine already runs the address, dropped."); return; }
                if (etarget == MPConfig.PlayerId) MergerEmployeeSync.ApplyOnOwner(p);
                else SendToPid(etarget, MessageEnvelope.Create(MessageType.MergerEmployeeEdit, "host", p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MergerStaff] HostRouteEmployeeEdit: {ex.Message}"); }
        }

        /// <summary>HOST (main thread), CROSS-HR-2 T2 and CROSS-HR-3 A2: route ONE leg about a co-member's REAL
        /// record - a training leg ("hrtrain") or a plan-tag write ("hrtag") - to the machine that holds it.
        /// What the host HAS is its own roster registry - MPRegisterSync.OwnerOfInjected, the map
        /// the host's own injected copies are indexed by; it answers only for records the host itself holds a
        /// copy of, so the leg also CARRIES the owner the runner read off its copy and the registry VERIFIES
        /// it when it knows better (the registry wins, and the disagreement is logged). Members only, and
        /// never back to the sender: the sender is the plan's runner, where the copy lives. A4: the plan's own
        /// runner is never named here - the leg is addressed by the RECORD's owner - so a STAND-IN running an
        /// absent runner's real plans sends these exactly as the runner would, and an absent WORKER's owner is
        /// reached through RouteTargetFor, which answers with the stand-in simulating that save.</summary>
        private static void HostRouteHrTrain(EmployeeEditPayload p, string senderPid)
        {
            try
            {
                string what = string.IsNullOrEmpty(p.Action) ? "hrtrain" : p.Action;
                string eid = p.EmployeeId ?? "";
                if (eid.Length == 0)
                { Plugin.Logger.LogWarning($"[CrossHR] {what} from '{senderPid}' names no employee - dropped."); return; }
                string known = ""; try { known = MPRegisterSync.OwnerOfInjected(eid); } catch { }
                if (known.Length > 0 && !string.IsNullOrEmpty(p.OwnerPid) && known != p.OwnerPid)
                    Plugin.Logger.LogWarning($"[CrossHR] {what} for '{eid}': the leg named owner '{p.OwnerPid}', the registry says '{known}' - the registry wins.");
                string ownerPid = known.Length > 0 ? known : (p.OwnerPid ?? "");
                if (ownerPid.Length == 0)
                { Plugin.Logger.LogWarning($"[CrossHR] {what} from '{senderPid}' for unknown employee '{eid}' - dropped."); return; }
                if (ownerPid == senderPid)
                { Plugin.Logger.LogWarning($"[CrossHR] {what} by '{senderPid}' for '{eid}' - that machine holds the record itself, dropped."); return; }
                if (!MergerSync.MergedRuntime(ownerPid, senderPid))
                { Plugin.Logger.LogWarning($"[CrossHR] {what} by '{senderPid}' for '{eid}' (owner '{ownerPid}') - not a co-member of the same company, dropped."); return; }
                // r1 MAJOR-1: WHERE that record lives NOW. The owner online -> the owner; away with a stand-in simulating
                // that save -> the stand-in (its lifted copy IS the live record); nobody -> dropped HERE with its own line,
                // never queued. SendToPid to an offline pid returns without a word - the silence this route must not have.
                string target = RouteTargetFor(p.AddressKey ?? "", ownerPid);
                if (target.Length == 0)
                {
                    Plugin.Logger.LogWarning($"[CrossHR] {what} for '{eid}' (owner '{ownerPid}', plan '{p.AssignedHrManagerPlanId}'): the owner is offline and nobody stands in - dropped ({(what == "hrtag" ? "that worker's HR plan tag did not change; the runner's own list entry is answered below" : "that worker's training for the day is lost; the runner paid for it once")}).");
                    // CROSS-HR-3b B2: a SET nobody can take is ANSWERED, so the runner undoes the list entry it
                    // made for it. A CLEAR needs no answer - it added nothing there to take back.
                    if (what == "hrtag" && !string.IsNullOrEmpty(p.AssignedHrManagerPlanId))
                        HostAnswerHrTagRefused(p, senderPid, "the owner is offline and nobody stands in");
                    return;
                }
                if (target == senderPid)
                { Plugin.Logger.LogWarning($"[CrossHR] {what} by '{senderPid}' for '{eid}' - that machine stands in for the owner and holds the record itself, dropped."); return; }
                if (target == MPConfig.PlayerId) MergerEmployeeSync.ApplyOnOwner(p);
                else SendToPid(target, MessageEnvelope.Create(MessageType.MergerEmployeeEdit, "host", p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossHR] HostRouteHrTrain: {ex.Message}"); }
        }

        /// <summary>HOST (main thread), CROSS-HR-3b B2: carry a REFUSED tag back to the machine whose HR plan
        /// holds the list entry - `OwnerPid` on the answer names that runner (it is the original sender of the
        /// tag leg). Members only, never back to the sender, and never queued: an undo for a runner that is not
        /// running is moot, because its plan is not running either.</summary>
        private static void HostRouteHrTagRefused(EmployeeEditPayload p, string senderPid)
        {
            try
            {
                string back = p.OwnerPid ?? "";
                string eid = p.EmployeeId ?? "";
                if (back.Length == 0)
                { Plugin.Logger.LogWarning($"[CrossHR] hrtag-refused from '{senderPid}' for '{eid}' names no runner to answer - dropped."); return; }
                if (back == senderPid)
                { Plugin.Logger.LogWarning($"[CrossHR] hrtag-refused by '{senderPid}' for '{eid}' - that machine is the runner itself, dropped."); return; }
                if (!MergerSync.MergedRuntime(back, senderPid))
                { Plugin.Logger.LogWarning($"[CrossHR] hrtag-refused by '{senderPid}' for '{eid}' (runner '{back}') - not a co-member of the same company, dropped."); return; }
                if (back == MPConfig.PlayerId) { MergerEmployeeSync.ApplyOnOwner(p); return; }
                if (PeerForPid(back) == null)
                { Plugin.Logger.LogWarning($"[CrossHR] hrtag-refused for '{eid}' (plan '{p.AssignedHrManagerPlanId}'): runner '{back}' is offline - dropped (its plan is not running either)."); return; }
                SendToPid(back, MessageEnvelope.Create(MessageType.MergerEmployeeEdit, "host", p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossHR] HostRouteHrTagRefused: {ex.Message}"); }
        }

        /// <summary>HOST, CROSS-HR-3b B2: the host's OWN refusal - a tag SET it could deliver to nobody. Same
        /// carrier, same Action, `Name` carrying the reason; the answer goes straight back to the sender (or is
        /// applied here when the host is that sender).</summary>
        private static void HostAnswerHrTagRefused(EmployeeEditPayload p, string senderPid, string why)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(senderPid)) return;
                var answer = new EmployeeEditPayload
                {
                    PlayerId   = MPConfig.PlayerId,
                    Action     = "hrtag-refused",
                    EmployeeId = p.EmployeeId ?? "",
                    OwnerPid   = senderPid,
                    AssignedHrManagerPlanId = p.AssignedHrManagerPlanId ?? "",
                    Name       = why ?? "",
                };
                if (senderPid == MPConfig.PlayerId) { MergerEmployeeSync.ApplyOnOwner(answer); return; }
                SendToPid(senderPid, MessageEnvelope.Create(MessageType.MergerEmployeeEdit, "host", answer));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossHR] HostAnswerHrTagRefused: {ex.Message}"); }
        }

        /// <summary>HOST (main thread), merger slice 6: fan ONE member's business-scoped pop-up out to
        /// every OTHER ONLINE member of THE SENDER'S merged company - and to the host itself when the
        /// host is a member of that same group. A non-member never receives it, and neither does a
        /// member of a DIFFERENT group: every hop is gated on MergerSync.MergedRuntime(sender, peer),
        /// which is true only for two pids inside one group. Serialize once, send per peer (the
        /// Broadcast idiom). The host's own sender calls this directly - no wire hop for the host.</summary>
        public static void HostRelayNotification(NotificationRelayPayload p, string senderPid)
        {
            try
            {
                if (string.IsNullOrEmpty(senderPid) || !NotificationRelay.PayloadSane(p, "host relay")) return;   // review #3/#6: bounded before fan-out
                if (!MergerSync.InAnyGroup(senderPid)) return;   // left the company between send and arrival
                byte[] bytes = null;
                int fanout = 0;
                foreach (var cp in ConnectedClientPeers())
                {
                    if (cp.playerId == senderPid) continue;                            // never back to the sender
                    if (!MergerSync.MergedRuntime(senderPid, cp.playerId)) continue;   // the sender's group only
                    if (bytes == null) bytes = MessageEnvelope.Create(MessageType.NotificationRelay, "host", p).Serialize();
                    cp.peer.Send(bytes, reliable: true);
                    fanout++;
                }
                if (MPConfig.PlayerId != senderPid && MergerSync.MergedRuntime(senderPid, MPConfig.PlayerId))
                { NotificationRelay.ApplyRelayed(p); fanout++; }                       // the host is a member too
                if (fanout == 0)
                    Plugin.Logger.LogInfo($"[NotifyRelay] '{p.HeaderKey}' from '{senderPid}' - no other online member to relay to.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[NotifyRelay] HostRelayNotification: {ex.Message}"); }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // SHARED-SHOP MANAGEMENT (the Business PERMISSION feature; src/SharedShopSchedule.cs) — NOT THE MERGER.
        // The merger's routes (HostRouteEmployeeEdit above, BusinessEditRequest) are untouched by this block.
        // Access here = a DIRECT Business grant from the shop's owner (GrantSync.IsGrantedDirect) - and, on the
        // routes merger phase 2 has reached, merger membership too, through the UNION check GrantSync.IsGranted:
        // the SCHEDULE routes (edit + session open/close, phase 0, 2026-09-10), the PRICE edit route (wave 1,
        // 2026-09-11) and the two READ relays, sales history and work info (wave 2, 2026-09-11). The staff and
        // work EDIT routes stay permission-only until waves 3-4. A per-sender rate cap (plan §2.9 P4) and a
        // payload shape check guard the relay; snapshots go to exactly one machine (P1).
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        private static readonly Dictionary<string, (float windowStart, int count)> _sharedRate = new();
        private const int SharedEditRatePerSecond     = 10;   // inbound editor traffic (edits, session open/close)
        private const int SharedSnapshotRatePerSecond = 30;   // owner → editor snapshots: a SEPARATE bucket, so an editor's opens can never starve the owner's replies
        private static bool SharedRateOk(string senderPid, string what, bool snapshotBucket = false)
        {
            float now = UnityEngine.Time.unscaledTime;
            string key = snapshotBucket ? senderPid + "|snap" : senderPid;
            int cap = snapshotBucket ? SharedSnapshotRatePerSecond : SharedEditRatePerSecond;
            if (_sharedRate.Count > 256)   // disconnected pids leave entries behind — sweep the idle ones occasionally
            {
                var stale = new List<string>();
                foreach (var kv in _sharedRate) if (now - kv.Value.windowStart > 60f) stale.Add(kv.Key);
                foreach (var k in stale) _sharedRate.Remove(k);
            }
            _sharedRate.TryGetValue(key, out var r);
            if (now - r.windowStart >= 1f) r = (now, 0);
            r.count++;
            _sharedRate[key] = r;
            if (r.count <= cap) return true;
            if (r.count == cap + 1)   // one line per burst, not one per dropped message
                Plugin.Logger.LogWarning($"[SharedShop] '{senderPid}' sent more than {cap} {what}s in a second — dropping the rest of this second (rate cap).");
            return false;
        }

        /// <summary>Resolve a shop's owner pid from the rental ledger; "" when unowned. This is the
        /// AUTHORISATION answer (whose grant/membership a sender must hold, and the identity every
        /// ask/answer pair checks) — never the delivery answer; RouteTargetFor below is that.</summary>
        private static string SharedShopOwnerPid(string addressKey)
        {
            if (!BuildingOwners.TryGetValue(addressKey, out var owner) || string.IsNullOrEmpty(owner)) return "";
            return owner == "host" ? MPConfig.PlayerId : owner;
        }

        /// <summary>MERGER PHASE 2 WAVE 3 (W3-0): WHERE a routed WRITE for this address must be delivered.
        /// The ledger owner when that player is ONLINE; else, while they are away, the machine simulating
        /// their businesses (its lifted copy IS the live state, and its owner-style pushes/publishes carry
        /// the edit onward); else "" — and "" means the caller REFUSES with the WARN logged here. Nothing is
        /// ever applied locally as a fallback. Before this, every write route sent to the ledger owner and a
        /// route to an offline owner was dropped by SendToPid without a word (the shipped price route
        /// included).</summary>
        public static string RouteTargetFor(string addressKey) => RouteTargetFor(addressKey, SharedShopOwnerPid(addressKey));

        private static string RouteTargetFor(string addressKey, string ownerPid)
        {
            if (string.IsNullOrEmpty(ownerPid)) return "";
            if (IsOnlinePid(ownerPid)) return ownerPid;
            try
            {
                // StableIdByPlayer survives a departure (it is not an online test, :5881), which is exactly
                // why it still answers for the absent owner the marks are keyed by.
                string stable = StableIdByPlayer.TryGetValue(ownerPid, out var s) && !string.IsNullOrEmpty(s) ? s : ownerPid;
                if (MergerAbsence.Marks.TryGetValue(stable, out var mark) && mark != null
                    && !string.IsNullOrEmpty(mark.SimulatorPid) && IsOnlinePid(mark.SimulatorPid))
                    return mark.SimulatorPid;
            }
            catch { }
            Plugin.Logger.LogWarning($"[Merger] route for '{addressKey}' refused: owner '{ownerPid}' offline and nobody simulating");
            return "";
        }

        /// <summary>W3-0 r1 (F7): an ASK — session open/close, sales history, work info, valuation — is answered
        /// by the LEDGER OWNER and nobody else: the simulator's lifted copy holds none of what those payloads
        /// carry, so a READ is never re-pointed at it (unlike a WRITE, which RouteTargetFor sends there). With the
        /// owner offline the send died inside SendToPid without a word, and the asking member's tab stayed blank
        /// and re-polled for ever. False = refused, with one line per asker+address+ask per minute so a 5 s poll
        /// cannot flood the log. No on-screen text: the tab stays exactly as it is. Main thread, like every
        /// host route (UnityEngine.Time, as SharedRateOk uses).</summary>
        private static readonly Dictionary<string, float> _askOffline = new Dictionary<string, float>();
        private static bool AskOwnerOnline(string ask, string addressKey, string askerPid, string ownerPid)
        {
            if (IsOnlinePid(ownerPid)) return true;
            try
            {
                float now = UnityEngine.Time.unscaledTime;
                string key = askerPid + "|" + addressKey + "|" + ask;
                if (_askOffline.Count > 256)
                {
                    var stale = new List<string>();
                    foreach (var kv in _askOffline) if (now - kv.Value > 60f) stale.Add(kv.Key);
                    foreach (var k in stale) _askOffline.Remove(k);
                }
                if (!_askOffline.TryGetValue(key, out var last) || now - last >= 60f)
                {
                    _askOffline[key] = now;
                    Plugin.Logger.LogWarning($"[SharedShop] {ask} for '{addressKey}' by '{askerPid}': owner '{ownerPid}' offline — no answer");
                }
            }
            catch { }
            return false;
        }

        /// <summary>Review #6: a Business grant is blanket across the owner's ESTABLISHED businesses — never
        /// empty premises, never a headquarters (rulings 24/27). The routes enforce the same per-address
        /// line the access push draws, so a crafted request cannot reach what the design excludes.
        /// PHASE 4c part 1 (H2): `mergedHq` is the MERGER exception and nothing else — the caller passes it
        /// only after MergerSync.MergedRuntime(owner, sender) has answered yes, so a headquarters opens for a
        /// COMPANY MEMBER and stays shut for a bare Business grant (rulings 24/27 untouched).</summary>
        private static bool SharedWorkAddressAllowed(string addressKey, bool mergedHq = false)
        {
            try
            {
                var reg = GameStatePatcher.FindRegistration(addressKey);
                string type = reg?.businessTypeName ?? "";
                if (type.Length == 0 || type == "ba:businesstype_empty") return false;
                if (type == "ba:businesstype_headquarters") return mergedHq;
                return true;
            }
            catch { return false; }
        }

        /// <summary>HOST (main thread): a permitted player's schedule edit — direct Business grant or merger membership
        /// (phase 0, 2026-09-10) — → the owner (applied here if the host owns it).</summary>
        public static void HostRouteSharedScheduleEdit(SharedScheduleEditPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(senderPid)) return;
                if (!SharedRateOk(senderPid, "schedule edit")) return;
                if (!SharedShopSchedule.ScheduleShapeOk(p.Days, out var shape))
                { Plugin.Logger.LogWarning($"[SharedShop] schedule edit by '{senderPid}' on '{p.AddressKey}' dropped at the host — {shape}."); return; }
                string ownerPid = SharedShopOwnerPid(p.AddressKey);
                if (ownerPid.Length == 0) { Plugin.Logger.LogWarning($"[SharedShop] schedule edit for unowned '{p.AddressKey}' from '{senderPid}' — dropped."); return; }
                if (ownerPid == senderPid) return;   // an owner's own edits never route
                if (!GrantSync.IsGrantedDirect(GrantKind.Business, ownerPid, senderPid) && !MergerSync.MergedRuntime(ownerPid, senderPid))
                { Plugin.Logger.LogWarning($"[SharedShop] schedule edit by '{senderPid}' on '{p.AddressKey}' (owner '{ownerPid}') — no Business permission or merger membership, dropped."); return; }
                string starget = RouteTargetFor(p.AddressKey, ownerPid);   // W3-0: owner, else their simulator, else refuse
                if (starget.Length == 0 || starget == senderPid) return;
                if (starget == MPConfig.PlayerId) SharedShopSchedule.ApplyOnOwner(p);
                else SendToPid(starget, MessageEnvelope.Create(MessageType.SharedScheduleEdit, "host", p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SharedShop] HostRouteSharedScheduleEdit: {ex.Message}"); }
        }

        /// <summary>HOST (main thread): editing-session traffic. open/close: the sender must hold a direct Business
        /// grant from the owner, or be merged with them (phase 0, 2026-09-10) → to the owner. snapshot: the sender must BE the owner → to exactly ONE machine (ToPid).</summary>
        public static void HostRouteScheduleSession(ScheduleSessionPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(senderPid)) return;
                if (!SharedRateOk(senderPid, p.Action == "snapshot" ? "snapshot" : "session message", snapshotBucket: p.Action == "snapshot")) return;
                if (!SharedShopSchedule.ScheduleShapeOk(p.Schedule, out var shapeAny))
                { Plugin.Logger.LogWarning($"[SharedShop] session '{p.Action}' for '{p.AddressKey}' from '{senderPid}' dropped at the host — {shapeAny}."); return; }
                string ownerPid = SharedShopOwnerPid(p.AddressKey);
                if (ownerPid.Length == 0) { Plugin.Logger.LogWarning($"[SharedShop] session '{p.Action}' for unowned '{p.AddressKey}' from '{senderPid}' — dropped."); return; }
                string target;
                switch (p.Action)
                {
                    case "open":
                    case "close":
                        if (ownerPid == senderPid) return;   // the owner has no session with themself
                        if (!GrantSync.IsGrantedDirect(GrantKind.Business, ownerPid, senderPid) && !MergerSync.MergedRuntime(ownerPid, senderPid))
                        { Plugin.Logger.LogWarning($"[SharedShop] session '{p.Action}' by '{senderPid}' on '{p.AddressKey}' (owner '{ownerPid}') — no Business permission or merger membership, dropped."); return; }
                        if (!AskOwnerOnline("session", p.AddressKey, senderPid, ownerPid)) return;   // W3-0 r1 (F7)
                        target = ownerPid;
                        break;
                    case "snapshot":
                        // W3-0 r1 (F3): the echo comes from the machine that RUNS the address — the ledger owner
                        // while they are online, else the simulator standing in for them. A REJECTED edit reverts
                        // on the helper by this very echo, so dropping the simulator's left the helper's copy wrong.
                        string runner = RouteTargetFor(p.AddressKey, ownerPid);
                        if (senderPid != runner)
                        { Plugin.Logger.LogWarning($"[SharedShop] schedule snapshot for '{p.AddressKey}' from '{senderPid}', which is not the machine running it ('{(runner.Length > 0 ? runner : "nobody")}'; ledger owner '{ownerPid}') — dropped."); return; }
                        if (string.IsNullOrEmpty(p.ToPid) || p.ToPid == senderPid) return;
                        target = p.ToPid;
                        break;
                    default:
                        return;
                }
                if (target == MPConfig.PlayerId) SharedShopSchedule.HandleSession(p);
                else SendToPid(target, MessageEnvelope.Create(MessageType.ScheduleSession, "host", p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SharedShop] HostRouteScheduleSession: {ex.Message}"); }
        }

        // ── Shared-shop slice 3: the owner's bench + routed assignment edits ──
        private static readonly Dictionary<string, SharedStaffPoolPayload> _sharedPoolByOwner = new();   // owner pid → last bench (replayed to newly granted players)

        /// <summary>HO-1c L2(b): which OTHER player's published bench holds this employee id ("" = none, or only
        /// the asker's own).  A bench is dropped when its owner leaves (:1773), so an answer also means that
        /// player is here to take the release leg.</summary>
        private static string BenchOwnerOf(string employeeId, string exceptPid)
        {
            try
            {
                if (string.IsNullOrEmpty(employeeId)) return "";
                foreach (var kv in _sharedPoolByOwner)
                {
                    if (kv.Key == exceptPid || kv.Value == null || kv.Value.Staff == null) continue;
                    foreach (var s in kv.Value.Staff)
                        if (s != null && s.Id == employeeId) return kv.Key;
                }
            }
            catch { }
            return "";
        }

        /// <summary>HOST (main thread): an owner's bench — cache it, hand it to every connected player who holds a
        /// Business key from that owner — M2 (2026-09-12): a direct grant OR co-membership, the same union the rest of
        /// the bench pipeline now reads — and to the host itself when it holds one. Never broadcast.</summary>
        // BENCH-MERGE-1: one bench line per SENDER per 30 s — publishes are periodic, so an always-on
        // line without a budget would be one entry per sender per publish for the whole session.
        private static readonly Dictionary<string, float> _benchLogAt = new();

        private static bool BenchLogDue(string senderPid)
        {
            try
            {
                float now = UnityEngine.Time.unscaledTime;
                if (_benchLogAt.TryGetValue(senderPid, out var next) && now < next) return false;
                _benchLogAt[senderPid] = now + 30f;
                return true;
            }
            catch { return true; }
        }

        public static void HostRouteSharedStaffPool(SharedStaffPoolPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(senderPid)) return;
                if (!SharedRateOk(senderPid, "bench publish")) return;
                if ((p.Staff?.Count ?? 0) > 200) { Plugin.Logger.LogWarning($"[SharedShop] bench from '{senderPid}': implausible count — dropped."); return; }
                _sharedPoolByOwner[senderPid] = p;
                // BENCH-MERGE-1 (bundle 20260918-211454): the whole hand-off used to sit inside
                // OwnerRunsManageableShop(sender). A MERGED member who owns no building of their own —
                // every one of the company's shops is the partner's — therefore had their unassigned
                // staff silently dropped, so a new hire never reached the partner who runs the shops.
                // Co-membership is the second gate: the same source of truth GrantSync.IsGranted unions
                // in (MergerSync.MergedRuntime), read host-side. The Business-key test is unchanged.
                int sent = 0, coMember = 0, grantedTo = 0;
                bool manageable = OwnerRunsManageableShop(senderPid);   // ONE city walk per publish; per player only the lookups below
                foreach (var pid in new List<string>(_peerNames.Values))
                {
                    if (pid == senderPid || !GrantSync.IsGranted(GrantKind.Business, senderPid, pid)) continue;   // M2: the union — a co-member holds the key too
                    grantedTo++;
                    bool co = false; try { co = MergerSync.MergedRuntime(senderPid, pid); } catch { }
                    if (co) coMember++;
                    if (!manageable && !co) continue;
                    SendToPid(pid, MessageEnvelope.Create(MessageType.SharedStaffPool, senderPid, p)); sent++;
                }
                if (senderPid != MPConfig.PlayerId && GrantSync.IsGranted(GrantKind.Business, senderPid, MPConfig.PlayerId))   // M2: the union
                {
                    grantedTo++;
                    bool coSelf = false; try { coSelf = MergerSync.MergedRuntime(senderPid, MPConfig.PlayerId); } catch { }
                    if (coSelf) coMember++;
                    if (manageable || coSelf) { SharedShopStaff.ApplyPool(p); sent++; }
                }
                // ALWAYS a line, including sent == 0: a bench that goes nowhere is the failure this
                // change exists to expose, and it used to be the one case that logged nothing.
                if (BenchLogDue(senderPid))
                    Plugin.Logger.LogInfo($"[SharedShop] bench of '{senderPid}' ({p.Staff?.Count ?? 0}): handed to {sent} player(s); gates manageableShop={manageable} coMember={coMember} granted={grantedTo}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SharedShop] HostRouteSharedStaffPool: {ex.Message}"); }
        }

        /// <summary>Does <paramref name="ownerPid"/> run at least one MANAGEABLE shop (a business that is neither empty
        /// nor a headquarters)? The bench only travels where it can be used — a Business grant on its own (helper access
        /// to a headquarters) does not ship the owner's staff list. ONE walk over the city per call; the per-player part
        /// of the question (the direct grant) is a dictionary lookup the callers make themselves.</summary>
        private static bool OwnerRunsManageableShop(string ownerPid)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerPid)) return false;
                var mine = new HashSet<string>();
                foreach (var kv in BuildingOwners)
                {
                    string o = kv.Value == "host" ? MPConfig.PlayerId : kv.Value;
                    if (o == ownerPid && !string.IsNullOrEmpty(kv.Key)) mine.Add(kv.Key);
                }
                if (mine.Count == 0) return false;
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return false;
                foreach (var reg in regs)
                {
                    if (reg == null) continue;
                    string a; try { a = GameStateReader.AddressKey(reg); } catch { continue; }
                    if (!mine.Contains(a)) continue;
                    string bt = ""; try { bt = reg.businessTypeName ?? ""; } catch { }
                    if (bt.Length > 0 && bt != "ba:businesstype_empty" && bt != "ba:businesstype_headquarters") return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>A newly granted player (or a rejoining one) gets the cached bench of every owner whose shop they may
        /// manage. <paramref name="manageKeys"/> is the SharedManageKeys set BuildBuildingAccessFor just computed for
        /// that player (direct grant + manageable type, per address) — it already answers "which owners", so this is
        /// dictionary lookups only, no walk over the city.</summary>
        private static void ReplaySharedPoolsTo(string clientPid, IEnumerable<string> manageKeys)
        {
            try
            {
                if (string.IsNullOrEmpty(clientPid) || manageKeys == null || _sharedPoolByOwner.Count == 0) return;
                var owners = new HashSet<string>();
                foreach (var a in manageKeys)
                    if (!string.IsNullOrEmpty(a) && BuildingOwners.TryGetValue(a, out var o) && !string.IsNullOrEmpty(o))
                        owners.Add(o == "host" ? MPConfig.PlayerId : o);
                owners.Remove(clientPid);
                foreach (var kv in _sharedPoolByOwner)
                {
                    if (!owners.Contains(kv.Key)) continue;
                    if (clientPid == MPConfig.PlayerId) SharedShopStaff.ApplyPool(kv.Value);
                    else SendToPid(clientPid, MessageEnvelope.Create(MessageType.SharedStaffPool, kv.Key, kv.Value));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SharedShop] ReplaySharedPoolsTo: {ex.Message}"); }
        }

        /// <summary>HOST (main thread): a permitted player's staff op on an owner's employee → the owner.
        /// assign/unassign (slice 3) plus wave 3's "raise". Merger phase 2 wave 3 (W3-1): the gate is the
        /// UNION check, so a company member acting on a merger-flipped partner shop reaches the owner
        /// instead of writing to their own replica.</summary>
        public static void HostRouteSharedStaffEdit(SharedStaffEditPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.EmployeeId) || string.IsNullOrEmpty(senderPid)) return;
                if (!SharedRateOk(senderPid, "staff edit")) return;
                string ownerPid = SharedShopOwnerPid(p.AddressKey);
                if (ownerPid.Length == 0) { Plugin.Logger.LogWarning($"[SharedShop] staff edit for unowned '{p.AddressKey}' from '{senderPid}' — dropped."); return; }
                if (ownerPid == senderPid) return;
                if (!GrantSync.IsGranted(GrantKind.Business, ownerPid, senderPid))   // wave 3 (W3-1): UNION — direct grant or merger membership
                { Plugin.Logger.LogWarning($"[SharedShop] staff edit by '{senderPid}' on '{p.AddressKey}' (owner '{ownerPid}') — no Business permission and not a company member, dropped."); return; }
                string ftarget = RouteTargetFor(p.AddressKey, ownerPid);   // W3-0
                if (ftarget.Length == 0) { Plugin.Logger.LogWarning($"[SharedShop] staff edit by '{senderPid}' on '{p.AddressKey}' (owner '{ownerPid}') — nobody runs that address right now (owner offline, no stand-in), dropped."); return; }
                if (ftarget == senderPid) return;
                if (ftarget == MPConfig.PlayerId) SharedShopStaff.ApplyOnOwner(p);
                else SendToPid(ftarget, MessageEnvelope.Create(MessageType.SharedStaffEdit, "host", p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SharedShop] HostRouteSharedStaffEdit: {ex.Message}"); }
        }

        // ═══ MERGER PHASE 4b (PEOPLE) part 1 (D20-1): THE SHARED CANDIDATE POOL ═══
        // The host is the only machine that can arbitrate a CLAIM, so it keeps two small tables: the last
        // pool each member published (replayed to a joiner, and the lookup that says whose candidate an id
        // is), and who currently holds each candidate. A claim is granted to the FIRST asker; it dies when
        // the holder releases it, goes offline, stops re-asserting it (the 30 s keepalive), or the owner
        // stops listing that candidate at all. Nothing here is persisted - a company's hiring race is a
        // session thing, exactly like the poach claim it is modelled on (RivalStaffSync).

        private static readonly Dictionary<string, CompanyCandidatesPayload> _candidatesByOwner = new();   // owner pid -> last pool
        private static readonly Dictionary<string, (string pid, float at)> _candidateClaims = new();       // candidateId -> holder + last assert
        private const float CandidateClaimStaleSeconds = 120f;   // 4x the claimant's 30 s keepalive

        /// <summary>HOST (main thread): one of the candidate-pool legs. Every leg is gated on the sender
        /// being in a company, and a claim additionally on the sender sharing THAT company with the
        /// candidate's origin. Refusals are logged, never silent.</summary>
        public static void HostRouteCompanyCandidates(CompanyCandidatesPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(senderPid)) return;
                if (!SharedRateOk(senderPid, "candidate message")) return;
                if (!MergerSync.InAnyGroup(senderPid))
                { Plugin.Logger.LogInfo($"[Candidates] '{p.Action}' from '{senderPid}' - not in a company, dropped."); return; }

                if (p.Action == "pool")
                {
                    var rows = p.Candidates ?? new List<CandidateRow>();
                    if (rows.Count > 100)
                    { Plugin.Logger.LogWarning($"[Candidates] pool from '{senderPid}': implausible count {rows.Count} - dropped."); return; }
                    p.OwnerPid = senderPid;                      // never take the sender's word for whose pool this is
                    var listed = new HashSet<string>();
                    foreach (var row in rows)
                    {
                        string cid = row?.Staff?.Id ?? "";
                        if (cid.Length == 0) continue;
                        listed.Add(cid);
                        row.ClaimedBy = _candidateClaims.TryGetValue(cid, out var cl) ? (cl.pid ?? "") : "";
                    }
                    // A claim on somebody this owner no longer lists (hired, declined, expired) is dead.
                    var dead = new List<string>();
                    foreach (var kv in _candidateClaims)
                        if (OwnerOfCandidate(kv.Key) == senderPid && !listed.Contains(kv.Key)) dead.Add(kv.Key);
                    _candidatesByOwner[senderPid] = p;
                    foreach (var cid in dead) _candidateClaims.Remove(cid);
                    FanOutCandidates(p, senderPid, includeOwner: false);
                    return;
                }

                string id = p.CandidateId ?? "";
                if (id.Length == 0) { Plugin.Logger.LogWarning($"[Candidates] '{p.Action}' from '{senderPid}' with no candidate id - dropped."); return; }

                // MERGER PHASE 4c PART 2a (D23): a PLAN-KEYED claim - today a staff-insurance OFFER id. No
                // pool lists it, so the owner cannot be looked up: the sender names it and the host checks
                // co-membership instead. Everything after that is the candidate claim, unchanged - the same
                // table, the same first-asker rule, the same staleness release.
                if (p.Action == "claim-plan" || p.Action == "release-plan")
                {
                    string oOwner = p.OwnerPid ?? "";
                    if (oOwner.Length == 0 || (oOwner != senderPid && !MergerSync.MergedRuntime(oOwner, senderPid)))
                    { Plugin.Logger.LogWarning($"[Candidates] '{p.Action}' by '{senderPid}' for offer '{id}' names '{oOwner}', who is not in that company - dropped."); return; }
                    if (p.Action == "release-plan")
                    {
                        if (!_candidateClaims.TryGetValue(id, out var ocur) || ocur.pid != senderPid)
                        { Plugin.Logger.LogInfo($"[Candidates] release of offer '{id}' by '{senderPid}' - they do not hold it, ignored."); return; }
                        _candidateClaims.Remove(id);
                        Plugin.Logger.LogInfo($"[Candidates] '{senderPid}' released insurance offer '{id}' - back to the company.");
                        FanOutCandidates(new CompanyCandidatesPayload { PlayerId = "host", Action = "verdict", OwnerPid = oOwner, CandidateId = id, ClaimedBy = "", Ok = true }, oOwner, includeOwner: true);
                        return;
                    }
                    _candidateClaims.TryGetValue(id, out var oheld);
                    string oholder = oheld.pid ?? "";
                    bool ofree = oholder.Length == 0 || oholder == senderPid || !IsOnlinePid(oholder)
                              || UnityEngine.Time.unscaledTime - oheld.at > CandidateClaimStaleSeconds;
                    if (!ofree)
                    {
                        Plugin.Logger.LogInfo($"[Candidates] claim of insurance offer '{id}' by '{senderPid}' REFUSED - '{oholder}' is already negotiating it.");
                        SendToPid(senderPid, MessageEnvelope.Create(MessageType.CompanyCandidates, "host",
                            new CompanyCandidatesPayload { PlayerId = "host", Action = "verdict", OwnerPid = oOwner, CandidateId = id, ClaimedBy = oholder, Ok = false }));
                        return;
                    }
                    _candidateClaims[id] = (senderPid, UnityEngine.Time.unscaledTime);
                    Plugin.Logger.LogInfo($"[Candidates] claim of insurance offer '{id}' (of '{oOwner}') GRANTED to '{senderPid}'.");
                    FanOutCandidates(new CompanyCandidatesPayload { PlayerId = "host", Action = "verdict", OwnerPid = oOwner, CandidateId = id, ClaimedBy = senderPid, Ok = true }, oOwner, includeOwner: true);
                    return;
                }

                string ownerPid = OwnerOfCandidate(id);
                if (ownerPid.Length == 0)
                { Plugin.Logger.LogWarning($"[Candidates] '{p.Action}' by '{senderPid}' for '{id}' - no member lists that candidate, dropped."); return; }
                if (ownerPid != senderPid && !MergerSync.MergedRuntime(ownerPid, senderPid))
                { Plugin.Logger.LogWarning($"[Candidates] '{p.Action}' by '{senderPid}' for '{id}' (of '{ownerPid}') - not in that company, dropped."); return; }

                if (p.Action == "claim")
                {
                    _candidateClaims.TryGetValue(id, out var held);
                    string holder = held.pid ?? "";
                    bool free = holder.Length == 0 || holder == senderPid || !IsOnlinePid(holder)
                             || UnityEngine.Time.unscaledTime - held.at > CandidateClaimStaleSeconds;
                    if (!free)
                    {
                        Plugin.Logger.LogInfo($"[Candidates] claim of '{id}' by '{senderPid}' REFUSED - '{holder}' is already talking to them.");
                        SendToPid(senderPid, MessageEnvelope.Create(MessageType.CompanyCandidates, "host",
                            new CompanyCandidatesPayload { PlayerId = "host", Action = "verdict", OwnerPid = ownerPid, CandidateId = id, ClaimedBy = holder, Ok = false }));
                        return;
                    }
                    _candidateClaims[id] = (senderPid, UnityEngine.Time.unscaledTime);
                    if (holder != senderPid)
                        Plugin.Logger.LogInfo($"[Candidates] claim of '{id}' (of '{ownerPid}') GRANTED to '{senderPid}'.");
                    FanOutCandidates(new CompanyCandidatesPayload { PlayerId = "host", Action = "verdict", OwnerPid = ownerPid, CandidateId = id, ClaimedBy = senderPid, Ok = true }, ownerPid, includeOwner: true);
                    return;
                }

                if (p.Action == "release")
                {
                    if (!_candidateClaims.TryGetValue(id, out var cur) || cur.pid != senderPid)
                    { Plugin.Logger.LogInfo($"[Candidates] release of '{id}' by '{senderPid}' - they do not hold it, ignored."); return; }
                    _candidateClaims.Remove(id);
                    Plugin.Logger.LogInfo($"[Candidates] '{senderPid}' released '{id}' - back in the company pool.");
                    FanOutCandidates(new CompanyCandidatesPayload { PlayerId = "host", Action = "verdict", OwnerPid = ownerPid, CandidateId = id, ClaimedBy = "", Ok = true }, ownerPid, includeOwner: true);
                    return;
                }

                if (p.Action == "accept")
                {
                    // T2 (review r1 MAJOR-3): the ACCEPT is the moment of commitment, so it is the moment the
                    // host reads. Nothing is cached - not even this member's own claim.
                    HostRouteCandidateAccept(p, senderPid, ownerPid);
                    return;
                }

                if (p.Action == "hired")
                {
                    _candidateClaims.Remove(id);
                    _candidateHired.Add(id);                  // T2: the consumed mark, so a later accept is refused
                    if (_candidatesByOwner.TryGetValue(ownerPid, out var pool) && pool?.Candidates != null)
                        pool.Candidates.RemoveAll(r => (r?.Staff?.Id ?? "") == id);
                    Plugin.Logger.LogInfo($"[Candidates] '{senderPid}' hired '{id}' out of '{ownerPid}'s pool - telling the origin to drop their record.");
                    // U3(b) r3: held for EVERY member that is offline, not only the origin (an unseen hired
                    // mark is how an orphan negotiation - and a second hire of one person - is born).
                    FanOutOrHoldCandidateMark(p, ownerPid);
                    FanOutCandidates(new CompanyCandidatesPayload { PlayerId = "host", Action = "verdict", OwnerPid = ownerPid, CandidateId = id, ClaimedBy = "", Ok = true }, ownerPid, includeOwner: true);
                    return;
                }

                Plugin.Logger.LogWarning($"[Candidates] unknown action '{p.Action}' from '{senderPid}' - dropped.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Candidates] HostRouteCompanyCandidates: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Re-stamp every row of a stored pool with the claim that stands RIGHT NOW (MINOR-7).</summary>
        private static void StampClaims(CompanyCandidatesPayload pool)
        {
            try
            {
                var rows = pool?.Candidates;
                if (rows == null) return;
                foreach (var row in rows)
                {
                    string cid = row?.Staff?.Id ?? "";
                    if (cid.Length == 0) continue;
                    row.ClaimedBy = _candidateClaims.TryGetValue(cid, out var cl) ? (cl.pid ?? "") : "";
                }
            }
            catch { }
        }

        /// <summary>Whose pool holds this candidate id ("" = nobody's, as far as the host has been told).</summary>
        private static string OwnerOfCandidate(string candidateId)
        {
            foreach (var kv in _candidatesByOwner)
            {
                var rows = kv.Value?.Candidates;
                if (rows == null) continue;
                for (int i = 0; i < rows.Count; i++)
                    if ((rows[i]?.Staff?.Id ?? "") == candidateId) return kv.Key;
            }
            return "";
        }

        /// <summary>Ship one candidate message to the ONLINE members of ownerPid's company. A POOL skips the
        /// owner (their own list is their own save); a VERDICT includes them, because the origin's real
        /// record must know it has been claimed.</summary>
        private static int FanOutCandidates(CompanyCandidatesPayload pay, string ownerPid, bool includeOwner)
        {
            int fanout = 0;
            try
            {
                if (!_running || pay == null || string.IsNullOrEmpty(ownerPid)) return 0;
                byte[]? bytes = null;
                foreach (var cp in ConnectedClientPeers())
                {
                    bool isOwner = cp.playerId == ownerPid;
                    if (isOwner && !includeOwner) continue;
                    if (!isOwner && !MergerSync.MergedRuntime(ownerPid, cp.playerId)) continue;
                    bytes ??= MessageEnvelope.Create(MessageType.CompanyCandidates, "host", pay).Serialize();
                    cp.peer.Send(bytes, reliable: true);
                    fanout++;
                }
                bool hostIsOwner = MPConfig.PlayerId == ownerPid;
                if ((includeOwner || !hostIsOwner) && (hostIsOwner || MergerSync.MergedRuntime(ownerPid, MPConfig.PlayerId)))
                { CompanyCandidates.Receive(pay); fanout++; }       // the host is a member too (already on the main thread)
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Candidates] fan-out: {ex.GetType().Name}: {ex.Message}"); }
            return fanout;
        }

        /// <summary>HOST: a JOINER's catch-up - every co-member's candidate pool as it stands now. Rides the
        /// same join replay the books, the feed and the agreement lists ride.</summary>
        public static void SendCompanyCandidatesTo(MPLink peer, string joinerPid)
        {
            try
            {
                if (string.IsNullOrEmpty(joinerPid)) return;
                int n = 0; bool sentMine = false;
                foreach (var kv in new List<KeyValuePair<string, CompanyCandidatesPayload>>(_candidatesByOwner))
                {
                    if (kv.Key == joinerPid || kv.Value == null) continue;
                    if (!MergerSync.MergedRuntime(kv.Key, joinerPid)) continue;
                    // MINOR-7: the STORED pool carries the claims as they stood when it was published. The
                    // joiner must see the claims as they stand NOW, so every row is re-stamped from the
                    // live table before the replay goes out.
                    StampClaims(kv.Value);
                    Send(peer, MessageEnvelope.Create(MessageType.CompanyCandidates, "host", kv.Value));
                    n++;
                    if (kv.Key == MPConfig.PlayerId) sentMine = true;
                }
                // r3 MINOR-2: the replay ALWAYS ends with a pool for the HOST's own pid, even an empty one.
                // A candidate-free company publishes nothing, and CompanyCandidates arms its orphan sweep on
                // the first pool that ARRIVES - without this it could never arm on a client.
                if (!sentMine && joinerPid != MPConfig.PlayerId && MergerSync.MergedRuntime(MPConfig.PlayerId, joinerPid))
                {
                    Send(peer, MessageEnvelope.Create(MessageType.CompanyCandidates, "host",
                        new CompanyCandidatesPayload { PlayerId = "host", Action = "pool", OwnerPid = MPConfig.PlayerId }));
                    n++;
                }
                if (n > 0) Plugin.Logger.LogInfo($"[Candidates] join replay to '{joinerPid}': {n} co-member pool(s).");
                HostFlushHeldCandidates(peer, joinerPid);   // T3: a hire notice held while they were away
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Candidates] join replay: {ex.GetType().Name}: {ex.Message}"); }
        }

        // === MERGER PHASE 4b (PEOPLE) P4 (D20-5): THE PHONE RELAY ===
        // Four legs, none of them persisted: a message is a moment, so there is no join replay and no
        // held notice - a member who was away simply never had that message. The host's whole job is the
        // group gate (a message never leaves the sender's company) and pointing a PRESS at the one machine
        // that still holds the button's live closure.

        /// <summary>HOST (main thread): one leg of the phone relay.</summary>
        public static void HostRouteCompanyMessages(CompanyMessagePayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(senderPid)) return;
                if (!CompanyMessages.PayloadSane(p, "host relay")) return;
                // r2 MAJOR-3: a PRESS the rate gate drops is ANSWERED, not silently swallowed. The presser's
                // own click already wiped that copy's buttons, and nothing else would ever clear its pending
                // entry - a silent drop meant that action could never be run again.
                // r4 MINOR-1: the OWNER'S ANSWER legs are EXEMPT from that bucket. A 'handled' or a
                // 'refused' is caused by a press that already passed the bucket itself, so neither can be an
                // abuse channel - and HostRefusePress answers only a 'press', so a dropped 'handled' was
                // SILENT: every partner copy then kept a dead button until somebody pressed it. A 'msg' the
                // bucket refuses is LOGGED instead (once per sender): a relay is a moment, nothing to retry.
                string mact = p.Action ?? "";
                if (mact != "handled" && mact != "refused" && !SharedRateOk(senderPid, "phone relay"))
                {
                    if (mact == "press") { HostRefusePress(p, senderPid, "busy"); return; }
                    if (_loggedMessageRateDrop.Add(senderPid))
                        Plugin.Logger.LogWarning($"[Messages] '{mact}' from '{senderPid}' dropped - that machine's phone traffic is over the cap ({SharedEditRatePerSecond}/s).");
                    return;
                }
                if (!MergerSync.InAnyGroup(senderPid))
                { Plugin.Logger.LogInfo($"[Messages] '{p.Action}' from '{senderPid}' - not in a company, dropped."); HostRefusePress(p, senderPid, "unknown"); return; }

                if (p.Action == "msg")
                {
                    p.PlayerId = senderPid; p.OwnerPid = senderPid;     // never take the sender's word for whose message this is
                    int fan = FanOutMessages(p, senderPid, includeOwner: false);
                    if (fan == 0 && _loggedNoMessagePeer.Add(senderPid))
                        Plugin.Logger.LogInfo($"[Messages] '{senderPid}' relayed a message but no co-member of theirs is online - nothing sent.");
                    return;
                }

                string owner = p.OwnerPid ?? "";
                if (owner.Length == 0)
                { Plugin.Logger.LogWarning($"[Messages] '{p.Action}' from '{senderPid}' names no owner - dropped."); HostRefusePress(p, senderPid, "unknown"); return; }
                if (owner != senderPid && !MergerSync.MergedRuntime(owner, senderPid))
                { Plugin.Logger.LogWarning($"[Messages] '{p.Action}' by '{senderPid}' for a message of '{owner}' - not in that company, dropped."); HostRefusePress(p, senderPid, "unknown"); return; }

                if (p.Action == "press")
                {
                    p.PlayerId = senderPid;                              // the presser, as the owner's log will name them
                    if (owner == MPConfig.PlayerId) { CompanyMessages.Receive(p); return; }
                    if (!IsOnlinePid(owner))
                    {
                        // r2 MAJOR-2: not just a log - the presser's copy has already cleared its own buttons,
                        // so it is told and puts them back; otherwise that action could never be run at all.
                        Plugin.Logger.LogWarning($"[Messages] press of '{p.MessageId}' by '{senderPid}' - '{owner}' is not online, so nobody can run that button; dropped.");
                        HostRefusePress(p, senderPid, "offline");
                        return;
                    }
                    SendToPid(owner, MessageEnvelope.Create(MessageType.CompanyMessages, "host", p));
                    _pressPending[(p.MessageId ?? "") + "|" + senderPid] = (owner, senderPid);   // r2 MAJOR-3
                    Plugin.Logger.LogInfo($"[Messages] press of '{p.MessageId}' by '{senderPid}' handed to the owner '{owner}'.");
                    return;
                }

                if (p.Action == "handled")
                {
                    if (owner != senderPid)
                    { Plugin.Logger.LogWarning($"[Messages] 'handled' for '{p.MessageId}' came from '{senderPid}', not the owner '{owner}' - dropped."); return; }
                    ClearPressPending(p.MessageId);                  // r2 MAJOR-3: that press is answered
                    FanOutMessages(p, owner, includeOwner: false);
                    return;
                }

                if (p.Action == "refused")
                {
                    // r2 MAJOR-2: the OWNER could not run a press (it no longer holds the message, the button
                    // is not there, or it had already been handled). This goes to the ONE presser, never to
                    // the company - nobody else's copy is waiting on an answer.
                    if (owner != senderPid)
                    { Plugin.Logger.LogWarning($"[Messages] 'refused' for '{p.MessageId}' came from '{senderPid}', not the owner '{owner}' - dropped."); return; }
                    string to = p.TargetPid ?? "";
                    if (to.Length == 0 || (to != MPConfig.PlayerId && !MergerSync.MergedRuntime(owner, to)))
                    { Plugin.Logger.LogWarning($"[Messages] 'refused' for '{p.MessageId}' names '{to}', who is not in that company - dropped."); return; }
                    ClearPressPending(p.MessageId);                  // r2 MAJOR-3: that press is answered
                    Plugin.Logger.LogInfo($"[Messages] the owner '{owner}' refused the press of '{p.MessageId}' ({p.Reason}) - telling '{to}'.");
                    if (to == MPConfig.PlayerId) { CompanyMessages.Receive(p); return; }
                    if (!IsOnlinePid(to)) { Plugin.Logger.LogInfo($"[Messages] '{to}' has gone, so the refusal of '{p.MessageId}' goes nowhere."); return; }
                    SendToPid(to, MessageEnvelope.Create(MessageType.CompanyMessages, "host", p));
                    return;
                }

                Plugin.Logger.LogWarning($"[Messages] unknown action '{p.Action}' from '{senderPid}' - dropped.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Messages] HostRouteCompanyMessages: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static readonly HashSet<string> _loggedNoMessagePeer = new();
        private static readonly HashSet<string> _loggedMessageRateDrop = new();   // r4 MINOR-1: one line per sender whose relay traffic the bucket refused

        /// <summary>HOST (r2 MAJOR-3): the presses handed to an owner and not yet answered, keyed
        /// "&lt;messageId&gt;|&lt;presser&gt;" so two members waiting on the same message are two entries. Main-thread
        /// only (every leg of the relay is enqueued there). Nothing is persisted - a press is a moment.</summary>
        private static readonly Dictionary<string, (string Owner, string Presser)> _pressPending = new();

        /// <summary>Every presser waiting on this message has been answered.</summary>
        private static void ClearPressPending(string messageId)
        {
            if (string.IsNullOrEmpty(messageId) || _pressPending.Count == 0) return;
            string prefix = messageId + "|";
            List<string> drop = null;
            foreach (var kv in _pressPending)
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) (drop ??= new List<string>()).Add(kv.Key);
            if (drop != null) foreach (var k in drop) _pressPending.Remove(k);
        }

        /// <summary>HOST (r2 MAJOR-3): a player has gone. Every press still waiting on a message THEY own is
        /// refused 'offline', so the presser's copy offers the button again instead of sitting "in flight" for
        /// the rest of the session; a press by the player who left simply drops out of the table.</summary>
        public static void HostForgetPressesOf(string pid)
        {
            try
            {
                if (string.IsNullOrEmpty(pid) || _pressPending.Count == 0) return;
                var drop = new List<string>();
                foreach (var kv in _pressPending)
                    if (kv.Value.Owner == pid || kv.Value.Presser == pid) drop.Add(kv.Key);
                int told = 0;
                foreach (var k in drop)
                {
                    var e = _pressPending[k];
                    _pressPending.Remove(k);
                    if (e.Presser == pid || e.Owner != pid) continue;        // the presser is the one who left
                    string mid = k.Substring(0, k.Length - e.Presser.Length - 1);
                    HostRefusePress(new CompanyMessagePayload { Action = "press", MessageId = mid, OwnerPid = e.Owner }, e.Presser, "offline");
                    told++;
                }
                if (drop.Count > 0)
                    Plugin.Logger.LogInfo($"[Messages] '{pid}' left with {drop.Count} press(es) in flight - {told} presser(s) still waiting were told.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Messages] forgetting the presses of '{pid}': {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST (r2 MAJOR-2): a PRESS this host cannot deliver is answered, not just logged. Only a
        /// press is answered this way - a 'msg' that reaches nobody is simply a message nobody was there to
        /// see, and the sender's own screen already has it.</summary>
        private static void HostRefusePress(CompanyMessagePayload p, string toPid, string reason)
        {
            try
            {
                if (p == null || p.Action != "press" || string.IsNullOrEmpty(toPid)) return;
                var back = new CompanyMessagePayload
                {
                    PlayerId  = MPConfig.PlayerId, Action = "refused", MessageId = p.MessageId ?? "",
                    OwnerPid  = p.OwnerPid ?? "",  TargetPid = toPid,  Reason = reason, ButtonIndex = p.ButtonIndex,
                };
                if (toPid == MPConfig.PlayerId) { CompanyMessages.Receive(back); return; }   // the host is the presser
                if (!IsOnlinePid(toPid)) return;
                SendToPid(toPid, MessageEnvelope.Create(MessageType.CompanyMessages, "host", back));
                Plugin.Logger.LogInfo($"[Messages] press of '{back.MessageId}' by '{toPid}' refused by the host ({reason}) - they put the buttons back.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Messages] refusing a press: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Ship one relay leg to the ONLINE members of ownerPid's company (the owner's own machine
        /// raised it, so it is skipped).</summary>
        private static int FanOutMessages(CompanyMessagePayload pay, string ownerPid, bool includeOwner)
        {
            int fanout = 0;
            try
            {
                if (!_running || pay == null || string.IsNullOrEmpty(ownerPid)) return 0;
                byte[]? bytes = null;
                foreach (var cp in ConnectedClientPeers())
                {
                    bool isOwner = cp.playerId == ownerPid;
                    if (isOwner && !includeOwner) continue;
                    if (!isOwner && !MergerSync.MergedRuntime(ownerPid, cp.playerId)) continue;
                    bytes ??= MessageEnvelope.Create(MessageType.CompanyMessages, "host", pay).Serialize();
                    cp.peer.Send(bytes, reliable: true);
                    fanout++;
                }
                bool hostIsOwner = MPConfig.PlayerId == ownerPid;
                if ((includeOwner || !hostIsOwner) && (hostIsOwner || MergerSync.MergedRuntime(ownerPid, MPConfig.PlayerId)))
                { CompanyMessages.Receive(pay); fanout++; }        // the host is a member too (already on the main thread)
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Messages] fan-out: {ex.GetType().Name}: {ex.Message}"); }
            return fanout;
        }


        // === MERGER PHASE 4b (PEOPLE) P2 r2 (T1): THE HOST-HELD TRANSFER ===
        // Review r1 MAJOR-1/2: a cross-member move used to be a straight line between the two machines -
        // the source removed its record and posted it at the destination. A destination that refused, or
        // that had gone, meant the record existed in NO save; a host restart mid-move meant the same.
        // The authority is now the host: it asks the source to RELEASE, HOLDS the record itself, relays
        // the adopt, and hands the record BACK to the source if the adopt refuses, the destination is
        // gone, or a whole game hour passes with no acknowledgement. The table is persisted in the
        // manifest, so a restart resumes it (MpManifest.Transfers).

        private class HostTransfer
        {
            public string TransferId = "", EmployeeId = "", FromAddr = "", ToAddr = "";
            public string SourcePid = "", DestPid = "", Stage = "requested", RelayedTo = "";
            public string InitiatorPid = "";   // r4 MINOR-3: who asked, so a source-side refusal can reach them at once
            public int Day, Hour, LastLogDay = -1, LastLogHour = -1;
            public EmployeeEditPayload? Record;
        }

        private static readonly Dictionary<string, HostTransfer> _transfers = new();

        /// <summary>U2 (re-check r3 MAJOR-2): transfer ids the host has GIVEN BACK, with the game day it
        /// happened. A late "adopted" naming one of these means the destination is holding a second live
        /// copy of an id the source already has, so it is answered with a "drop" leg. Only ids the host
        /// actually returned are in here: an "adopted" for an id that simply closed normally is ignored, so
        /// a duplicated acknowledgement can never destroy a record that legitimately moved.</summary>
        private static readonly Dictionary<string, int> _transfersReturned = new();

        private static int GameDayNow() { try { return SaveGameManager.Current?.Day ?? 0; } catch { return 0; } }
        private static int GameHourNow() { try { return SaveGameManager.Current?.Hour ?? 0; } catch { return 0; } }
        private static int GameHoursSince(int day, int hour) => (GameDayNow() - day) * 24 + (GameHourNow() - hour);

        /// <summary>Deliver one employee payload to the machine that runs an address: the host applies it
        /// itself, anyone else gets it on the wire. Held nowhere - the caller keeps the transfer entry.</summary>
        private static void SendEmployeeEditToPid(string pid, EmployeeEditPayload p)
        {
            if (string.IsNullOrEmpty(pid)) return;
            if (pid == MPConfig.PlayerId) MergerEmployeeSync.ApplyOnOwner(p);
            else SendToPid(pid, MessageEnvelope.Create(MessageType.MergerEmployeeEdit, "host", p));
        }

        private static EmployeeEditPayload TransferLeg(HostTransfer t, string action, string addressKey, string otherKey)
        {
            var p = (action == "adopt-in" || action == "return") && t.Record != null
                  ? MergerEmployeeSync.CloneRecord(t.Record)
                  : new EmployeeEditPayload();
            p.PlayerId = "host";
            p.Action = action;
            p.EmployeeId = t.EmployeeId;
            p.TransferId = t.TransferId;
            p.AddressKey = addressKey ?? "";
            p.OtherAddressKey = otherKey ?? "";
            return p;
        }

        /// <summary>HOST (main thread): every leg of a host-held transfer. Idempotent by (TransferId,
        /// stage) at both ends - a repeated leg re-acknowledges and changes nothing.</summary>
        public static void HostRouteTransfer(EmployeeEditPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(senderPid)) return;
                string tid = p.TransferId ?? "";
                if (tid.Length == 0)
                { Plugin.Logger.LogWarning($"[Transfer] '{p.Action}' from '{senderPid}' with no transfer id - dropped."); return; }

                if (p.Action == "transfer-request")
                {
                    if (_transfers.ContainsKey(tid)) return;                       // a resend of the ask
                    // An EMPTY from-end means the REQUESTER holds the record itself (their own employee, or
                    // one on their bench): there is no partner roster to check it against, and the source
                    // runner is the asker. A NAMED from-end is a partner's shop and is checked like the
                    // destination - registered, granted to the asker, and run by somebody right now.
                    string from = p.AddressKey ?? "", to = p.OtherAddressKey ?? "";
                    if (to.Length == 0)
                    { Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: a transfer needs a destination."); return; }
                    if (!BuildingOwners.TryGetValue(to, out var toRaw) || string.IsNullOrEmpty(toRaw))
                    { Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: '{to}' is not a registered business here."); return; }
                    string toOwner = toRaw == "host" ? MPConfig.PlayerId : toRaw;
                    if (toOwner != senderPid && !GrantSync.IsGranted(GrantKind.Business, toOwner, senderPid))
                    { Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: '{senderPid}' does not hold the destination '{to}'."); return; }
                    string src = senderPid;
                    if (from.Length > 0)
                    {
                        if (!BuildingOwners.TryGetValue(from, out var fo) || string.IsNullOrEmpty(fo))
                        { Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: '{from}' is not a registered business here."); return; }
                        string fromOwner = fo == "host" ? MPConfig.PlayerId : fo;
                        if (fromOwner != senderPid && !GrantSync.IsGranted(GrantKind.Business, fromOwner, senderPid))
                        { Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: '{senderPid}' does not hold the source '{from}'."); return; }
                        src = RouteTargetFor(from, fromOwner);
                    }
                    else
                    {
                        // HO-1c L2(b): an empty from-end is usually the asker's own record - but a member may now
                        // place a copy off a PARTNER's bench, and the OWNER holds that record.  The published
                        // bench says whose it is, and that player becomes the source runner, so the release leg
                        // goes to them.  The asker's own bench still answers "" here and is refused below as the
                        // ordinary routed assign it is.
                        string benchOwner = BenchOwnerOf(p.EmployeeId ?? "", senderPid);
                        // HO-1d (re-review MAJOR-1): the same source check the named-from branch makes above - the
                        // asker must hold that owner's bench (a co-member, or a helper the owner granted), or a
                        // stale/crafted leg naming any published bench id would make the host order a release.
                        if (benchOwner.Length > 0 && benchOwner != senderPid
                            && !GrantSync.IsGranted(GrantKind.Business, benchOwner, senderPid))
                        { Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: '{senderPid}' does not hold '{benchOwner}' bench."); return; }
                        if (benchOwner.Length > 0)
                        {
                            src = benchOwner;
                            Plugin.Logger.LogInfo($"[Transfer] {tid}: '{p.EmployeeId}' stands on '{benchOwner}' bench - they release, not '{senderPid}'.");
                        }
                    }
                    string dst = RouteTargetFor(to, toOwner);
                    if (src.Length == 0 || dst.Length == 0)
                    { Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: nobody is running '{(src.Length == 0 ? from : to)}' - nothing released."); return; }
                    if (src == dst)
                    { Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: one machine runs both ends - that move is an ordinary routed assign."); return; }
                    var t = new HostTransfer { TransferId = tid, EmployeeId = p.EmployeeId ?? "", FromAddr = from, ToAddr = to,
                                               SourcePid = src, DestPid = dst, Stage = "requested", InitiatorPid = senderPid,
                                               Day = GameDayNow(), Hour = GameHourNow() };
                    _transfers[tid] = t;
                    Plugin.Logger.LogInfo($"[Transfer] {tid}: requested ('{t.EmployeeId}' {from} -> {to}); asking '{src}' to release.");
                    SendEmployeeEditToPid(src, TransferLeg(t, "release", from, to));
                    return;
                }

                if (!_transfers.TryGetValue(tid, out var e))
                {
                    if (p.Action == "adopted")
                    {
                        // U2 (MAJOR-2): the entry is gone. If the host GAVE THE RECORD BACK before this
                        // acknowledgement arrived, the sender is holding a duplicate of a live id and must
                        // drop it; if the entry simply closed on a successful adopt, this is a repeat and is
                        // ignored (dropping on a repeat would destroy a record that moved correctly).
                        if (_transfersReturned.ContainsKey(tid))
                        {
                            Plugin.Logger.LogWarning($"[Transfer] {tid}: 'adopted' from '{senderPid}' names a transfer the host had already returned - telling them to drop the record.");
                            SendEmployeeEditToPid(senderPid, new EmployeeEditPayload
                            {
                                PlayerId = "host", Action = "drop", EmployeeId = p.EmployeeId ?? "", TransferId = tid,
                                AddressKey = p.AddressKey ?? "", OtherAddressKey = p.OtherAddressKey ?? "",
                            });
                        }
                        return;
                    }
                    if (p.Action == "returned" || p.Action == "dropped") return;   // an ack for an entry already closed
                    Plugin.Logger.LogWarning($"[Transfer] {tid}: '{p.Action}' from '{senderPid}' names no transfer the host holds - dropped.");
                    return;
                }

                if (p.Action == "released")
                {
                    if (senderPid != e.SourcePid)
                    { Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: 'released' came from '{senderPid}', not the source '{e.SourcePid}'."); return; }
                    // U2 (MINOR-1): the STAGE decides what a release means. One that lands while the host is
                    // already relaying an adopt or a give-back is a duplicate - the host holds the record
                    // either way, so it is acknowledged as already-held and nothing is relayed twice.
                    if (e.Stage == "adopting" || e.Stage == "returning" || e.Stage == "released")
                    { Plugin.Logger.LogInfo($"[Transfer] {tid}: 'released' arrived while the host is already {e.Stage} - the record is already held here; nothing re-relayed."); return; }
                    // U2 (MAJOR-5): the "requested" deadline CANCELS the entry instead of dropping it, so a
                    // release that raced that deadline still finds its transfer. It is taken in and handed
                    // straight back to the machine that let go - never discarded with the record gone.
                    if (e.Stage == "cancelled")
                    {
                        if (e.Record == null) e.Record = MergerEmployeeSync.CloneRecord(p);
                        e.Day = GameDayNow(); e.Hour = GameHourNow();
                        Plugin.Logger.LogWarning($"[Transfer] {tid}: 'released' landed after the host had cancelled the move - held and handed straight back to '{senderPid}'.");
                        RelayReturn(e);
                        return;
                    }
                    if (e.Record == null)
                    {
                        e.Record = MergerEmployeeSync.CloneRecord(p);
                        e.Day = GameDayNow(); e.Hour = GameHourNow();
                        e.Stage = "released";
                        Plugin.Logger.LogInfo($"[Transfer] {tid}: released to the host by '{senderPid}' - the host is the only holder now.");
                    }
                    RelayAdopt(e);
                    return;
                }

                if (p.Action == "adopted")
                {
                    // U2 (MAJOR-2): only the machine the adopt was RELAYED TO, while the entry is still
                    // adopting, may close it. A late acknowledgement (the host had already timed the adopt
                    // out and given the record back) would otherwise close the entry with the id live in TWO
                    // saves - payroll is id-keyed, so it would be paid twice. That sender is told to drop.
                    bool tooLate = e.Stage == "returning" || e.Stage == "cancelled"
                                || (e.Stage == "adopting" && e.RelayedTo.Length > 0 && senderPid != e.RelayedTo);
                    if (tooLate)
                    {
                        Plugin.Logger.LogWarning($"[Transfer] {tid}: 'adopted' from '{senderPid}' arrived while the host is {e.Stage} - too late; telling them to drop the record.");
                        SendEmployeeEditToPid(senderPid, TransferLeg(e, "drop", e.ToAddr, e.FromAddr));
                        return;
                    }
                    if (e.Stage != "adopting")
                    { Plugin.Logger.LogWarning($"[Transfer] {tid}: 'adopted' from '{senderPid}' while the host is {e.Stage} - nothing was ever relayed to them, ignored."); return; }
                    _transfers.Remove(tid);
                    Plugin.Logger.LogInfo($"[Transfer] {tid}: ADOPTED by '{senderPid}' at '{e.ToAddr}' - one save holds the record again.");
                    return;
                }

                if (p.Action == "release-refused")
                {
                    // r4 MINOR-3 (rig run, build A): a source-side release refusal used to be a LOCAL log
                    // only, so the host sat on a "requested" entry for a whole game hour and the initiator's
                    // copy sat at the destination until its own 30 s give-back. The source answers now, the
                    // entry closes at once, and the initiator is told so their copy goes home immediately.
                    if (senderPid != e.SourcePid)
                    { Plugin.Logger.LogWarning($"[Transfer] {tid}: 'release-refused' came from '{senderPid}', not the source '{e.SourcePid}' - ignored."); return; }
                    if (e.Stage != "requested" && e.Stage != "cancelled")
                    { Plugin.Logger.LogWarning($"[Transfer] {tid}: 'release-refused' arrived while the host is {e.Stage} - the record is already in the air; ignored."); return; }
                    _transfers.Remove(tid);
                    Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: the source '{senderPid}' would not release '{e.EmployeeId}' - the move is off (nothing was ever released).");
                    if (e.InitiatorPid.Length > 0 && e.InitiatorPid != senderPid)
                        SendEmployeeEditToPid(e.InitiatorPid, TransferLeg(e, "transfer-refused", e.FromAddr, e.ToAddr));
                    return;
                }

                if (p.Action == "transfer-refused")
                {
                    e.Day = GameDayNow(); e.Hour = GameHourNow(); e.RelayedTo = "";
                    Plugin.Logger.LogWarning($"[Transfer] {tid}: refused: '{senderPid}' could not take '{e.EmployeeId}' at '{e.ToAddr}' - going back to the source.");
                    RelayReturn(e);
                    return;
                }

                if (p.Action == "returned")
                {
                    _transfers.Remove(tid);
                    _transfersReturned[tid] = GameDayNow();   // U2: a late "adopted" for this id must be dropped, not closed
                    Plugin.Logger.LogInfo($"[Transfer] {tid}: RETURNED to '{senderPid}' - nothing was lost.");
                    return;
                }

                if (p.Action == "dropped")
                {
                    // U2: the late adopter has removed its duplicate. The record is back to exactly one save.
                    Plugin.Logger.LogInfo($"[Transfer] {tid}: '{senderPid}' dropped the copy it adopted too late - one save holds the record again.");
                    return;
                }

                Plugin.Logger.LogWarning($"[Transfer] {tid}: unknown leg '{p.Action}' from '{senderPid}' - dropped.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Transfer] HostRouteTransfer: {ex.Message}"); }
        }

        private static string RunnerOf(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return "";
            if (!BuildingOwners.TryGetValue(addressKey, out var o) || string.IsNullOrEmpty(o)) return "";
            return RouteTargetFor(addressKey, o == "host" ? MPConfig.PlayerId : o);
        }

        private static void RelayAdopt(HostTransfer e)
        {
            string dst = RunnerOf(e.ToAddr);
            if (dst.Length == 0)
            {
                Plugin.Logger.LogWarning($"[Transfer] {e.TransferId}: refused: nobody runs '{e.ToAddr}' any more - going back to the source.");
                e.RelayedTo = "";
                RelayReturn(e);
                return;
            }
            e.DestPid = dst; e.RelayedTo = dst; e.Stage = "adopting";
            Plugin.Logger.LogInfo($"[Transfer] {e.TransferId}: adopt relayed to '{dst}'.");
            SendEmployeeEditToPid(dst, TransferLeg(e, "adopt-in", e.ToAddr, e.FromAddr));
        }

        private static void RelayReturn(HostTransfer e)
        {
            e.Stage = "returning";
            // U2 (MINOR-2): the give-back goes to the machine that RELEASED, whenever it is here - not to
            // whoever happens to run the from-address NOW. Only when the releaser has gone does the record
            // go to the stand-in running that address, which adopts it tagged to the absent owner exactly as
            // the absence installer does. With no from-address and the releaser away, the host keeps holding.
            // SourcePid is never overwritten: it is what the "released" gate is checked against.
            string src = (e.SourcePid == MPConfig.PlayerId || IsOnlinePid(e.SourcePid)) ? e.SourcePid
                       : (e.FromAddr.Length > 0 ? RunnerOf(e.FromAddr) : "");
            if (src.Length == 0)
            {
                if (e.LastLogDay != GameDayNow() || e.LastLogHour != GameHourNow())
                {
                    e.LastLogDay = GameDayNow(); e.LastLogHour = GameHourNow();
                    Plugin.Logger.LogWarning($"[Transfer] {e.TransferId}: refused: neither '{e.SourcePid}' nor anybody running '{e.FromAddr}' is here - the host keeps '{e.EmployeeId}' until one of them is back.");
                }
                return;
            }
            // The deadline clock restarts on every offer, so a give-back that cannot land is re-offered once
            // a GAME hour instead of on every tick for ever.
            e.Day = GameDayNow(); e.Hour = GameHourNow();
            e.RelayedTo = src;
            SendEmployeeEditToPid(src, TransferLeg(e, "return", e.FromAddr, e.ToAddr));
        }

        /// <summary>HOST, MAIN THREAD (the shared-staff tick): the recurring check the design asks for -
        /// events drive the happy path, and this only catches what never answered. One GAME hour is the
        /// deadline, read from the game's own clock, so a paused world never expires anything.</summary>
        public static void HostTransfersTick()
        {
            HostCargoTick();   // 4c part 2: the routed cargo table is swept on the same host sweep
            try
            {
                if (_transfersReturned.Count > 0)
                {
                    var oldTids = new List<string>();
                    foreach (var kv in _transfersReturned) if (GameDayNow() - kv.Value >= 1) oldTids.Add(kv.Key);
                    foreach (var k in oldTids) _transfersReturned.Remove(k);
                }
                if (!_running || _transfers.Count == 0) return;
                foreach (var e in new List<HostTransfer>(_transfers.Values))
                {
                    if (e == null) continue;
                    if (e.Stage == "adopting")
                    {
                        string dst = RunnerOf(e.ToAddr);
                        if (dst.Length > 0 && dst != e.RelayedTo) { RelayAdopt(e); continue; }   // a restored entry, or the runner changed
                    }
                    if (GameHoursSince(e.Day, e.Hour) < 1) continue;
                    if (e.Stage == "requested")
                    {
                        // U2 (MAJOR-5): do NOT drop the entry. A release can still be in the air, and an
                        // entry the host has forgotten turns that release into "names no transfer the host
                        // holds - dropped" with the record already gone from the source's save. The entry is
                        // CANCELLED and kept for one game day, long enough for a late release to find it and
                        // be handed straight back.
                        e.Stage = "cancelled"; e.Day = GameDayNow(); e.Hour = GameHourNow();
                        Plugin.Logger.LogWarning($"[Transfer] {e.TransferId}: refused: the source never released '{e.EmployeeId}' within a game hour - nothing moved (the entry is kept a game day in case the release is still in the air).");
                        continue;
                    }
                    if (e.Stage == "cancelled")
                    {
                        if (GameHoursSince(e.Day, e.Hour) >= 24)
                        {
                            _transfers.Remove(e.TransferId);
                            Plugin.Logger.LogInfo($"[Transfer] {e.TransferId}: the cancelled entry for '{e.EmployeeId}' is a game day old - forgotten (nothing was ever released).");
                        }
                        continue;
                    }
                    if (e.Stage == "adopting")
                    {
                        Plugin.Logger.LogWarning($"[Transfer] {e.TransferId}: refused: no adopt acknowledgement within a game hour - going back to the source.");
                        e.Day = GameDayNow(); e.Hour = GameHourNow();
                        RelayReturn(e);
                        continue;
                    }
                    if (e.Stage == "returning") RelayReturn(e);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Transfer] host tick: {ex.Message}"); }
        }

        /// <summary>TestDrive `transfers`: one line per in-transit record the host holds.</summary>
        public static List<string> TransfersReadout()
        {
            var outp = new List<string>();
            try
            {
                foreach (var e in _transfers.Values)
                    if (e != null) outp.Add($"{e.TransferId}:{e.EmployeeId} {e.FromAddr}->{e.ToAddr} stage={e.Stage}");
            }
            catch { }
            return outp;
        }

        /// <summary>Merger phase 4b (people) P2: the in-transit table for the manifest MODEL, beside the
        /// absence marks it rides with. The RECORD travels as text (the payload serialized).</summary>
        public static List<MpTransferEntry> SnapshotTransfers()
        {
            var list = new List<MpTransferEntry>();
            try
            {
                foreach (var e in _transfers.Values)
                {
                    if (e == null || string.IsNullOrEmpty(e.TransferId)) continue;
                    string json = "";
                    try { if (e.Record != null) json = Newtonsoft.Json.JsonConvert.SerializeObject(e.Record); } catch { }
                    list.Add(new MpTransferEntry
                    {
                        TransferId = e.TransferId, EmployeeId = e.EmployeeId,
                        FromAddressKey = e.FromAddr, ToAddressKey = e.ToAddr,
                        SourcePid = e.SourcePid, DestPid = e.DestPid,
                        Stage = e.Stage, Day = e.Day, Hour = e.Hour, RecordJson = json,
                    });
                }
                list.Sort((x, y) => string.CompareOrdinal(x.TransferId, y.TransferId));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Transfer] manifest snapshot: {ex.Message}"); }
            return list;
        }

        /// <summary>Host: REPLACE the in-transit table from the manifest being restored - clear-then-apply
        /// beside the absence restore, same timeline rule. An entry with no record (it never got past
        /// "requested") is dropped: nothing was released, so there is nothing to lose. Every restored
        /// entry resumes on the next tick - re-relayed to whoever runs the destination now, else
        /// returned to the source.</summary>
        public static void RestoreTransfersFromManifest(MpManifest m)
        {
            try
            {
                _transfers.Clear();
                _transfersReturned.Clear();   // U2: the give-back memo belongs to the table it guards
                int n = 0;
                if (m?.Transfers != null)
                    foreach (var t in m.Transfers)
                    {
                        if (string.IsNullOrEmpty(t?.TransferId)) continue;
                        EmployeeEditPayload? rec = null;
                        try { if (!string.IsNullOrEmpty(t.RecordJson)) rec = Newtonsoft.Json.JsonConvert.DeserializeObject<EmployeeEditPayload>(t.RecordJson); } catch { }
                        if (rec == null) continue;                       // nothing was ever released under this id
                        _transfers[t.TransferId] = new HostTransfer
                        {
                            TransferId = t.TransferId, EmployeeId = t.EmployeeId ?? "",
                            FromAddr = t.FromAddressKey ?? "", ToAddr = t.ToAddressKey ?? "",
                            SourcePid = t.SourcePid ?? "", DestPid = t.DestPid ?? "",
                            Stage = t.Stage == "returning" || t.Stage == "cancelled" ? t.Stage : "adopting",
                            Day = t.Day, Hour = t.Hour, Record = rec, RelayedTo = "",
                        };
                        n++;
                    }
                if (n > 0) Plugin.Logger.LogInfo($"[Transfer] restored {n} in-transit entr{(n == 1 ? "y" : "ies")} from the manifest.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Transfer] manifest restore: {ex.Message}"); }
        }

        // === MERGER PHASE 4c PART 2: THE ROUTED CARGO TRANSFER (host relay + in-transit table) ===

        /// <summary>One cargo transfer the host is holding. The goods are REAL and are nowhere else
        /// while Stage is "delivering": the source has already withdrawn them from its warehouse and no
        /// destination has shelved them yet, so this record is the only thing that knows they exist -
        /// which is why it is stamped into the per-slot manifest beside the absence marks. Items is the
        /// payload as the source sent it (name, amount, price per unit - exactly what the native
        /// detached CargoInstance carries), kept whole so a give-back can name every unit.</summary>
        private sealed class HostCargo
        {
            public string TransferId = "", PlanId = "", SourceKey = "", DestKey = "";
            public string SourcePid = "", RelayedTo = "", Stage = "delivering";
            public int Day, Hour, LastLogDay = -1, LastLogHour = -1;
            public int LastOfferLogDay = -1, LastOfferLogHour = -1;   // r4 (I5): the repeat-offer note has its OWN game-hour throttle - it shared the close leg's pair, so either one silenced the other for the rest of the hour
            public bool Rerouted;                       // C6: a deliver is re-pointed at a NEW runner exactly once
            public bool Redelivered;                    // r2 (F2): the ONE last deliver attempt before a whole-record give-back
            public bool WithdrawAgain;                  // r2 (F2): a LATE ack after a give-back - the close leg asks the source to withdraw again
            public string SentCloseTo = "";             // r2 (F3/F4): who the ack/return went to last - the only pid a "closed" is taken from
            public long Seq;                            // r3 (H1): the monotonic stamp the world-load union decides by
            public List<CargoTransferItem> Items = new();
        }

        private static readonly Dictionary<string, HostCargo> _cargo = new();

        /// <summary>r3 (H1): the table above reached DISK only at a save, and while a record is "delivering"
        /// it is the only thing in the world that knows the goods exist - the source has already withdrawn
        /// them. Every mutation therefore stamps the record with the next Seq and writes the whole table to
        /// cargo-transit.bamp.json at once (a handful of times a game hour, a few KB). Seq is also the
        /// "newer" test the world-load union needs when the manifest and the file both hold one id.</summary>
        private static long _cargoSeq;

        private static void CargoChanged(HostCargo? e)
        {
            if (e != null) e.Seq = ++_cargoSeq;
            MPSaveCoordinator.PersistCargoTransitNow();
        }

        /// <summary>r2 (F4): what the host ANSWERED under one transfer id, remembered from the need leg so
        /// the offer can be checked against it. Without it the host stored whatever amounts arrived, from
        /// anybody: SenderIs (:2233) only proves the payload's PlayerId is the real sender, never that the
        /// sender is the machine the need was answered for.</summary>
        private sealed class HostCargoNeed
        {
            public string SourcePid = "", DestRunner = "";
            public readonly Dictionary<string, int> Answered = new();
        }

        private static readonly Dictionary<string, HostCargoNeed> _cargoNeed = new();

        /// <summary>r2 (F2): records the host has already given back WHOLE. An acknowledgement that arrives
        /// afterwards means the destination shelved units that have since gone home - they exist twice, and
        /// the source is asked to withdraw them again under a derived id.</summary>
        private static readonly Dictionary<string, HostCargo> _cargoGaveBack = new();

        /// <summary>Hand one cargo leg to the machine that must run it: this machine applies it itself,
        /// anyone else gets it on the wire. The host never keeps a leg - the table above is the state.</summary>
        private static void SendCargoToPid(string pid, CargoTransferPayload p)
        {
            if (string.IsNullOrEmpty(pid)) return;
            p.TargetPid = pid;
            if (pid == MPConfig.PlayerId) CargoTransfer.Receive(p);
            else SendToPid(pid, MessageEnvelope.Create(MessageType.CargoTransfer, "host", p));
        }

        private static CargoTransferPayload CargoLeg(HostCargo e, string action, string reason)
        {
            var p = new CargoTransferPayload
            {
                PlayerId = "host", Action = action, TransferId = e.TransferId, PlanId = e.PlanId,
                SourceKey = e.SourceKey, DestKey = e.DestKey, SourcePid = e.SourcePid,
                Day = e.Day, Hour = e.Hour, Reason = reason ?? "",
            };
            foreach (var it in e.Items)
                if (it != null) p.Items.Add(new CargoTransferItem { ItemName = it.ItemName, Amount = it.Amount, PricePerUnit = it.PricePerUnit, Remainder = it.Remainder });
            return p;
        }

        /// <summary>HOST, MAIN THREAD. Every inbound leg of a routed cargo transfer. The host stamps
        /// SourcePid and TargetPid itself and never takes the sender's word for either; a leg whose two
        /// ends are not in one merged company is dropped. Stage is the authority: an "offer" for an id
        /// already held is a resend, and an "ack" for an id already closed is answered by silence.</summary>
        public static void HostRouteCargoTransfer(CargoTransferPayload p, string senderPid)
        {
            try
            {
                if (p == null) return;
                string tid = p.TransferId ?? "";
                if (tid.Length == 0) { Plugin.Logger.LogWarning($"[Cargo] a leg from '{senderPid}' carries no transfer id - dropped."); return; }

                if (p.Action == CargoTransfer.ActNeed && !p.Answer)
                {
                    string runner = RouteTargetFor(p.DestKey ?? "");
                    // r3 (H2): SourceKey was copied unchecked, and every close leg goes to whoever runs it -
                    // so a member could name a CO-MEMBER's warehouse as the source and have that warehouse
                    // shelve goods it never sent, remainder included. The sender must run the source, by the
                    // same runner test the close leg uses.
                    string srcRunner = RunnerOf(p.SourceKey ?? "");
                    if (runner.Length == 0 || srcRunner != senderPid || !CargoEndsAreOneCompany(senderPid, p.SourceKey ?? "", p.DestKey ?? ""))
                    {
                        var refusal = new CargoTransferPayload
                        {
                            PlayerId = "host", Action = CargoTransfer.ActNeed, Answer = true, TransferId = tid,
                            PlanId = p.PlanId, SourceKey = p.SourceKey, DestKey = p.DestKey,
                            Day = p.Day, Hour = p.Hour,
                            Reason = runner.Length == 0 ? "nobody is running that destination"
                                   : srcRunner != senderPid ? "the sender does not run that source"
                                   : "the two ends are not in one company",
                        };
                        Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: {refusal.Reason} ('{p.DestKey}') - nothing is withdrawn.");
                        SendCargoToPid(senderPid, refusal);
                        return;
                    }
                    // r2 (F4): the ask BINDS this id to one source and one destination runner.
                    if (_cargoNeed.Count > 2000) _cargoNeed.Clear();
                    _cargoNeed[tid] = new HostCargoNeed { SourcePid = senderPid, DestRunner = runner };
                    p.PlayerId = "host"; p.SourcePid = senderPid;
                    SendCargoToPid(runner, p);
                    return;
                }

                if (p.Action == CargoTransfer.ActNeed && p.Answer)
                {
                    // r2 (F4): the answer may come ONLY from the runner the host asked, and goes ONLY to the
                    // source that asked - never to whatever TargetPid the payload happens to name.
                    _cargoNeed.TryGetValue(tid, out var nd);
                    if (nd == null)
                    { Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: a need ANSWER from '{senderPid}' for an id the host never asked about - dropped."); return; }
                    if (senderPid != nd.DestRunner)
                    { Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: the need answer came from '{senderPid}', not from '{nd.DestRunner}' who was asked - dropped."); return; }
                    string to = nd.SourcePid;
                    if (to.Length == 0 || (to != MPConfig.PlayerId && !IsOnlinePid(to)))
                    { Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: the need answer is for '{to}', who is not here - dropped (nothing was withdrawn)."); return; }
                    // r3 (H3): the cap is per ITEM, SUMMED over the rows. An answer carrying one item twice
                    // used to overwrite, and each offered row was then tested alone against that figure.
                    nd.Answered.Clear();
                    foreach (var it in p.Items ?? new List<CargoTransferItem>())
                        if (it != null && !string.IsNullOrEmpty(it.ItemName) && it.Amount > 0)
                            nd.Answered[it.ItemName] = (nd.Answered.TryGetValue(it.ItemName, out var had) ? had : 0) + it.Amount;
                    p.PlayerId = "host"; p.TargetPid = to;
                    SendCargoToPid(to, p);
                    return;
                }

                if (p.Action == CargoTransfer.ActOffer)
                {
                    if (_cargo.TryGetValue(tid, out var have) && have != null)
                    {
                        if (senderPid != have.SourcePid)
                        { Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: a repeat offer from '{senderPid}', not from the source '{have.SourcePid}' - dropped."); return; }
                        // r3 (H4): only a record still DELIVERING may be re-relayed. A stale offer for one
                        // already acked or returning re-entered the delivering path, and with nobody on the
                        // destination that hands the WHOLE record back although its share is already shelved.
                        // Those two stages hold their own close leg and re-send it on the sweep by themselves.
                        if (have.Stage == "delivering") { RelayCargoDeliver(have); return; }   // a resend of the offer: re-relay, never re-store
                        if (have.LastOfferLogDay != GameDayNow() || have.LastOfferLogHour != GameHourNow())
                        {
                            have.LastOfferLogDay = GameDayNow(); have.LastOfferLogHour = GameHourNow();   // r4 (I5): its own pair
                            Plugin.Logger.LogInfo($"[Cargo] transfer {tid}: a repeat offer for a record already '{have.Stage}' - ignored; the outcome is already on its way home.");
                        }
                        return;
                    }
                    // r2 (F4): only the machine the need was answered FOR may offer, and only amounts the
                    // host actually answered. A refusal here must still be a RETURN - the goods are already
                    // out of the warehouse, so dropping the leg would destroy them.
                    _cargoNeed.TryGetValue(tid, out var need);
                    string bad = "";
                    if (need == null) bad = "the host answered no need under that id";
                    else if (senderPid != need.SourcePid) bad = $"the offer came from '{senderPid}', not from the source '{need.SourcePid}' the need was answered for";
                    else
                    {
                        // r3 (H3): SUM the offered rows per item before the test - two rows of one item, each
                        // inside the answered figure, together carried twice the need.
                        var offered = new Dictionary<string, int>();
                        foreach (var it in p.Items ?? new List<CargoTransferItem>())
                        {
                            if (it == null || string.IsNullOrEmpty(it.ItemName) || it.Amount <= 0) continue;
                            offered[it.ItemName] = (offered.TryGetValue(it.ItemName, out var had) ? had : 0) + it.Amount;
                        }
                        foreach (var kv in offered)
                        {
                            int answered = need.Answered.TryGetValue(kv.Key, out var a) ? a : 0;
                            if (kv.Value > answered) { bad = $"'{kv.Key}' x{kv.Value} was offered but only {answered} was answered"; break; }
                        }
                    }
                    var e = new HostCargo
                    {
                        TransferId = tid, PlanId = p.PlanId ?? "", SourceKey = p.SourceKey ?? "",
                        DestKey = p.DestKey ?? "", SourcePid = senderPid, Stage = "delivering",
                        Day = GameDayNow(), Hour = GameHourNow(),
                    };
                    // r4 (I2): the host AGGREGATES the offered rows per item name - ONE record per name, in
                    // the order the names first appear, priced by the first row of that name. The source
                    // aggregates already (CargoTransfer.cs), but a modified client need not, and two records
                    // of one name both matched the SAME row of the acknowledgement below: one remainder was
                    // applied to both, creating or destroying units. A row with no name at all is dropped -
                    // nothing can be acknowledged, returned or booked under an empty name.
                    var byName = new Dictionary<string, CargoTransferItem>();
                    int noName = 0;
                    foreach (var it in p.Items ?? new List<CargoTransferItem>())
                    {
                        if (it == null || it.Amount <= 0) continue;
                        if (string.IsNullOrEmpty(it.ItemName)) { noName++; continue; }
                        if (byName.TryGetValue(it.ItemName, out var row) && row != null) { row.Amount += it.Amount; continue; }
                        row = new CargoTransferItem { ItemName = it.ItemName, Amount = it.Amount, PricePerUnit = it.PricePerUnit };
                        byName[it.ItemName] = row; e.Items.Add(row);
                    }
                    if (noName > 0) Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: {noName} offered row(s) from '{senderPid}' carry no item name - dropped.");
                    if (e.Items.Count == 0) { Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: the offer from '{senderPid}' carries nothing - dropped."); return; }
                    if (bad.Length > 0)
                    {
                        Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: {bad} (sender '{senderPid}') - the goods go straight back.");
                        _cargo[tid] = e; CargoChanged(e);                      // held until the source confirms "closed"
                        RelayCargoReturn(e, "the offer did not match what the host answered", false);
                        return;
                    }
                    _cargo[tid] = e; CargoChanged(e);                          // r3 (H1): on disk before the relay, not at the next save
                    Plugin.Logger.LogInfo($"[Cargo] transfer {tid}: {e.Items.Count} item(s) in transit from '{e.SourceKey}' to '{e.DestKey}' - held by the host.");
                    RelayCargoDeliver(e);
                    return;
                }

                if (p.Action == CargoTransfer.ActDeliver)
                {
                    // r2 (F4): a deliver is host-originated. Accepting one from a member would let anybody
                    // conjure goods onto any destination's shelves.
                    Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: 'deliver' is host-originated and never arrives from a member (sender '{senderPid}') - dropped.");
                    return;
                }

                if (p.Action == CargoTransfer.ActAck)
                {
                    HostCargo? e = null;
                    if (!_cargo.TryGetValue(tid, out e) || e == null) _cargoGaveBack.TryGetValue(tid, out e);
                    if (e == null)
                    { Plugin.Logger.LogInfo($"[Cargo] transfer {tid}: an acknowledgement arrived for a record the host no longer holds - dropped."); return; }
                    if (senderPid != e.RelayedTo)
                    { Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: the acknowledgement came from '{senderPid}', not from '{e.RelayedTo}' the deliver was relayed to - dropped."); return; }
                    // r4 (I2): the acknowledgement is matched by UNIQUE item name - the FIRST row of a name
                    // wins, exactly as the inner scan here used to - against a record table that now holds
                    // one row per name, so no remainder can land on two records of the same item.
                    var ackBy = new Dictionary<string, int>();
                    foreach (var ai in p.Items ?? new List<CargoTransferItem>())
                        if (ai != null && !string.IsNullOrEmpty(ai.ItemName) && !ackBy.ContainsKey(ai.ItemName)) ackBy[ai.ItemName] = ai.Remainder;
                    int shelved = 0;
                    for (int i = 0; i < e.Items.Count; i++)
                    {
                        var src = e.Items[i]; if (src == null) continue;
                        // r4b: the remainder is CLAMPED to [0, Amount] here as well as on the source's close (CargoTransfer.cs
                        // ~:541-543) - a forged acknowledgement can neither owe negative units nor claim more back than left.
                        if (!string.IsNullOrEmpty(src.ItemName) && ackBy.TryGetValue(src.ItemName, out var rem)) src.Remainder = Math.Max(0, Math.Min(rem, src.Amount));
                        int got = src.Amount - src.Remainder; if (got > 0) shelved += got;
                    }
                    if (e.Stage == "returning" || e.WithdrawAgain)
                    {
                        // r2 (F2) THE LATE ACKNOWLEDGEMENT: the whole record has already gone home, so what
                        // the destination shelved now exists twice. The undo rides a DERIVED id, because the
                        // give-back's own id is (or is about to be) marked closed on the source.
                        if (shelved <= 0)
                        { Plugin.Logger.LogInfo($"[Cargo] transfer {tid}: a late acknowledgement after a give-back, but the destination shelved nothing - nothing to undo."); return; }
                        string againId = tid + "|again";
                        if (_cargo.ContainsKey(againId))
                        { Plugin.Logger.LogInfo($"[Cargo] transfer {tid}: a late acknowledgement after a give-back - the withdraw-again is already in flight."); return; }
                        Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: LATE acknowledgement after a give-back - {shelved} unit(s) may now exist twice; asking the source to withdraw them again.");
                        var again = new HostCargo
                        {
                            TransferId = againId, PlanId = e.PlanId, SourceKey = e.SourceKey, DestKey = e.DestKey,
                            SourcePid = e.SourcePid, RelayedTo = e.RelayedTo, Stage = "acked", WithdrawAgain = true,
                            Day = GameDayNow(), Hour = GameHourNow(),
                        };
                        foreach (var it in e.Items)
                            if (it != null) again.Items.Add(new CargoTransferItem { ItemName = it.ItemName, Amount = it.Amount, PricePerUnit = it.PricePerUnit, Remainder = it.Remainder });
                        _cargoGaveBack.Remove(tid);
                        _cargo[againId] = again; CargoChanged(again);
                        SendCargoCloseLeg(again, CargoTransfer.ActReturn, "a late acknowledgement after a give-back", true);
                        return;
                    }
                    // r2 (F2/F3): an ACKED record is HELD and persisted - the host keeps offering the
                    // acknowledgement to whoever runs the source until that machine confirms "closed".
                    e.Stage = "acked"; e.WithdrawAgain = false; e.SentCloseTo = "";
                    CargoChanged(e);                                           // r3 (H1): the shelved figures and the stage, on disk now
                    SendCargoCloseLeg(e, CargoTransfer.ActAck, p.Reason ?? "", true);
                    return;
                }

                if (p.Action == CargoTransfer.ActClosed)
                {
                    if (!_cargo.TryGetValue(tid, out var e) || e == null)
                    { Plugin.Logger.LogInfo($"[Cargo] transfer {tid}: a 'closed' arrived for a record the host no longer holds - nothing to drop."); return; }
                    if (senderPid != e.SentCloseTo)
                    { Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: 'closed' came from '{senderPid}', not from '{e.SentCloseTo}' the leg was sent to - dropped."); return; }
                    _cargo.Remove(tid); _cargoNeed.Remove(tid); CargoChanged(null);
                    // _cargoGaveBack deliberately KEEPS a whole give-back: that is exactly the record a late
                    // acknowledgement has to be matched against.
                    Plugin.Logger.LogInfo($"[Cargo] transfer {tid}: confirmed closed by '{senderPid}' - the host's record is dropped.");
                    return;
                }

                Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: unknown leg '{p.Action}' from '{senderPid}' - dropped.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] HostRouteCargoTransfer: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Both ends of a cargo leg must belong to ONE merged company: the sender, whoever owns
        /// the destination, and (r3 H2) whoever owns the SOURCE - an unchecked source let a member name a
        /// co-member's warehouse as the origin. Same discipline as the phone relay's owner check.</summary>
        private static bool CargoEndsAreOneCompany(string senderPid, string sourceKey, string destKey)
        {
            try
            {
                string owner = SharedShopOwnerPid(destKey);
                if (owner.Length == 0) return false;
                if (!(owner == senderPid || MergerSync.MergedRuntime(owner, senderPid))) return false;
                string src = SharedShopOwnerPid(sourceKey);
                if (src.Length == 0) return false;
                return src == owner || MergerSync.MergedRuntime(src, owner);
            }
            catch { return false; }
        }

        /// <summary>HOST: hand the in-transit goods to whoever runs the destination NOW. With nobody
        /// there the whole record goes home to the source - nothing is ever left in the gap.</summary>
        private static void RelayCargoDeliver(HostCargo e)
        {
            string dst = RunnerOf(e.DestKey);
            if (dst.Length == 0)
            { Plugin.Logger.LogWarning($"[Cargo] transfer {e.TransferId}: refused: nobody runs '{e.DestKey}' any more - the goods go back to the source."); RelayCargoReturn(e, "nobody runs the destination", false); return; }
            e.Stage = "delivering"; e.RelayedTo = dst;
            e.Day = GameDayNow(); e.Hour = GameHourNow();
            CargoChanged(e);                                     // r3 (H1): the stage and the clock, on disk before the leg goes out
            SendCargoToPid(dst, CargoLeg(e, CargoTransfer.ActDeliver, ""));
            Plugin.Logger.LogInfo($"[Cargo] transfer {e.TransferId}: deliver relayed to '{dst}' for '{e.DestKey}'.");
        }

        /// <summary>HOST (r2 F2/F3): hand the CLOSE leg - an acknowledgement or a give-back - to whoever
        /// RUNS the source now. The record is NOT dropped here: it is dropped only when that machine answers
        /// "closed", so a leg aimed at a machine that goes away is simply offered again next sweep. The leg
        /// is self-contained, so a stand-in runner that never had the source's row can still apply it.
        /// remaindersOnly=false is the whole-record give-back and is used ONLY for a record that never
        /// acknowledged - after an ack, every unit the destination shelved stays shelved.</summary>
        private static void SendCargoCloseLeg(HostCargo e, string action, string why, bool remaindersOnly)
        {
            string back = e.SourceKey.Length > 0 ? RunnerOf(e.SourceKey) : "";
            if (back.Length == 0 && (e.SourcePid == MPConfig.PlayerId || IsOnlinePid(e.SourcePid))) back = e.SourcePid;
            if (back.Length == 0)
            {
                if (e.LastLogDay != GameDayNow() || e.LastLogHour != GameHourNow())
                {
                    e.LastLogDay = GameDayNow(); e.LastLogHour = GameHourNow();
                    Plugin.Logger.LogWarning($"[Cargo] transfer {e.TransferId}: refused: nobody is running '{e.SourceKey}' - the host keeps the goods until one of them is back.");
                }
                return;
            }
            var leg = CargoLeg(e, action, why ?? "");
            leg.RemaindersOnly = remaindersOnly;
            leg.WithdrawAgain  = e.WithdrawAgain;
            bool resend = e.SentCloseTo == back;
            e.SentCloseTo = back;
            SendCargoToPid(back, leg);
            if (resend)
            {
                if (e.LastLogDay != GameDayNow() || e.LastLogHour != GameHourNow())
                {
                    e.LastLogDay = GameDayNow(); e.LastLogHour = GameHourNow();
                    Plugin.Logger.LogInfo($"[Cargo] transfer {e.TransferId}: the acknowledgement was re-sent to '{back}' - still waiting for it to be confirmed closed.");
                }
                return;
            }
            Plugin.Logger.LogInfo($"[Cargo] transfer {e.TransferId}: {(remaindersOnly ? "acknowledgement" : "give-back")} handed to '{back}'{(string.IsNullOrEmpty(why) ? "" : " (" + why + ")")} - waiting for 'closed'.");
        }

        /// <summary>HOST: give a record back to the source. One that NEVER acknowledged goes back WHOLE and
        /// is remembered, in case its acknowledgement is merely late (r2 F2); one that DID acknowledge
        /// carries the remainders only, so nothing the destination shelved comes home as well.</summary>
        private static void RelayCargoReturn(HostCargo e, string why, bool remaindersOnly)
        {
            e.Stage = remaindersOnly ? "acked" : "returning";
            if (!remaindersOnly)
            {
                if (_cargoGaveBack.Count > 2000) _cargoGaveBack.Clear();
                _cargoGaveBack[e.TransferId] = e;
            }
            CargoChanged(e);                                     // r3 (H1)
            SendCargoCloseLeg(e, CargoTransfer.ActReturn, why, remaindersOnly);
        }

        /// <summary>HOST, MAIN THREAD (C6). Events drive the happy path; this catches what never
        /// answered. A destination whose RUNNER changed (an absence hand-over) gets the deliver once
        /// more; a record with no acknowledgement after one GAME day goes home. The clock is the game's
        /// own, so a paused world expires nothing.</summary>
        private static void HostCargoTick()
        {
            try
            {
                if (!_running || _cargo.Count == 0) return;
                foreach (var e in new List<HostCargo>(_cargo.Values))
                {
                    if (e == null) continue;
                    // r2 (F3): a close leg that has not been CONFIRMED is offered again every sweep, to
                    // whoever runs the source NOW - which is how an outcome survives its runner going away.
                    if (e.Stage == "acked")
                    { SendCargoCloseLeg(e, e.WithdrawAgain ? CargoTransfer.ActReturn : CargoTransfer.ActAck, e.WithdrawAgain ? "a late acknowledgement after a give-back" : "", true); continue; }
                    if (e.Stage == "returning")
                    { SendCargoCloseLeg(e, CargoTransfer.ActReturn, "nobody could take the goods", false); continue; }
                    string dst = RunnerOf(e.DestKey);
                    if (dst.Length > 0 && dst != e.RelayedTo && !e.Rerouted)
                    { e.Rerouted = true; RelayCargoDeliver(e); continue; }   // a restored record, or the runner changed - once
                    if (GameDayNow() - e.Day < 1) continue;
                    // r2 (F2): ONE last deliver to any reachable runner before the whole record goes home -
                    // a give-back is the only leg that cannot be undone if the delivery did in fact happen.
                    // The clock is NOT restamped, so the give-back follows on the very next sweep.
                    if (dst.Length > 0 && !e.Redelivered)
                    {
                        e.Redelivered = true; e.RelayedTo = dst;
                        SendCargoToPid(dst, CargoLeg(e, CargoTransfer.ActDeliver, ""));
                        Plugin.Logger.LogWarning($"[Cargo] transfer {e.TransferId}: no acknowledgement within a game day - deliver replayed to '{dst}' once before giving the record back.");
                        continue;
                    }
                    RelayCargoReturn(e, "no acknowledgement within a game day", false);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] host tick: {ex.Message}"); }
        }

        /// <summary>TestDrive `cargo`: one line per in-transit cargo record the host holds.</summary>
        public static List<string> CargoReadout()
        {
            var outp = new List<string>();
            try
            {
                foreach (var e in _cargo.Values)
                    if (e != null) outp.Add($"{e.TransferId}:{e.SourceKey}->{e.DestKey} items={e.Items.Count} stage={e.Stage}");
            }
            catch { }
            return outp;
        }

        /// <summary>Merger phase 4c part 2: the in-transit CARGO table for the manifest MODEL, beside
        /// the employee transfers it rides with. The items travel as text (the list serialized).</summary>
        public static List<MpCargoTransferEntry> SnapshotCargoTransfers()
        {
            var list = new List<MpCargoTransferEntry>();
            try
            {
                foreach (var e in _cargo.Values)
                {
                    if (e == null || string.IsNullOrEmpty(e.TransferId)) continue;
                    string json = "";
                    try { json = Newtonsoft.Json.JsonConvert.SerializeObject(e.Items); } catch { }
                    list.Add(new MpCargoTransferEntry
                    {
                        TransferId = e.TransferId, PlanId = e.PlanId,
                        SourceAddressKey = e.SourceKey, DestAddressKey = e.DestKey,
                        SourcePid = e.SourcePid, Stage = e.Stage, Day = e.Day, Hour = e.Hour,
                        ItemsJson = json, WithdrawAgain = e.WithdrawAgain, Seq = e.Seq,
                    });
                }
                list.Sort((x, y) => string.CompareOrdinal(x.TransferId, y.TransferId));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] manifest snapshot: {ex.Message}"); }
            return list;
        }

        /// <summary>r3 (H1): the same table for its OWN file (cargo-transit.bamp.json), stamped with the
        /// host's high-water Seq at the moment of the write - which is what lets the world-load union tell
        /// "this id was dropped after that manifest was written" from "this id is simply not in the file".
        /// r4 (I3): the file's BaseSaveStamp - which SAVE this tail continues - is the coordinator's to fill
        /// in, because it is carried unchanged across every write between two manifest writes.</summary>
        public static MpCargoTransitState SnapshotCargoTransit()
            => new MpCargoTransitState { Seq = _cargoSeq, Transfers = SnapshotCargoTransfers() };

        /// <summary>Host: REPLACE the in-transit cargo table from the manifest being restored, UNIONED with
        /// this host's own transit file WHEN that file continues this very save (r4 I3: the file is per
        /// lineage BASE and the manifest is one VARIANT of the lineage, so the stamps must agree; a tail of
        /// an abandoned timeline is discarded and the manifest alone is the truth) - by TransferId, the
        /// higher Seq winning -
        /// clear-then-apply beside the employee transfers, the same timeline rule. An entry carrying no
        /// items is dropped (nothing was ever withdrawn under that id). Every restored entry resumes on
        /// the next tick: re-relayed to whoever runs the destination now, else returned to the source -
        /// which is why a member's own save never has to carry a unit that is in the air.</summary>
        public static void RestoreCargoTransfersFromManifest(MpManifest m)
        {
            try
            {
                _cargo.Clear(); _cargoSeq = 0;
                string mine = m?.SaveStamp ?? "";
                // r3 (H1) THE UNION. The manifest is written at a save; the transit file at every change.
                // One id can be in both, and the HIGHER Seq is the newer. An id the file has already seen
                // stamped (Seq <= the file's high-water) but does NOT list was closed and dropped after that
                // manifest was written, so it is not resurrected. Entries with no stamp at all (a pre-r3
                // manifest) are kept: the ids are idempotent on both ends either way.
                var best = new Dictionary<string, MpCargoTransferEntry>();
                if (m?.CargoTransfers != null)
                    foreach (var t in m.CargoTransfers)
                        if (t != null && !string.IsNullOrEmpty(t.TransferId)) best[t.TransferId] = t;
                var transit = MPSaveCoordinator.ReadCargoTransitNow();
                bool hadFile = transit != null;
                // r4 (I1): the high-water is the COUNTER, not a property of the records that survived.
                // Rebuilding it from the restored entries alone threw it away, and the rewrite at the end
                // then published {Seq:0}: every tombstone the file held stopped being one, so a crash before
                // the next save let the NEXT load resurrect ids that had already been delivered and closed.
                if (transit != null && transit.Seq > _cargoSeq) _cargoSeq = transit.Seq;
                // r4 (I3) WHICH SAVE DOES THE TAIL CONTINUE? The file is per lineage BASE - one live tail
                // per playthrough - while the manifest being loaded is one VARIANT of that lineage. A tail
                // written after a DIFFERENT save belongs to a timeline this load abandons: in the world now
                // being loaded those records' sources still hold the goods, so unioning it delivers them a
                // second time (and its high-water would drop this variant's own entries as well). Only a
                // tail stamped with THIS manifest's SaveStamp continues this save; a manifest with no stamp
                // at all (written before the field existed) counts as another timeline.
                if (transit != null && (mine.Length == 0 || transit.BaseSaveStamp != mine))
                {
                    Plugin.Logger.LogWarning($"[Cargo] the live transit tail continues a different save ('{transit.BaseSaveStamp}' vs '{mine}') - discarded; the loaded manifest is the truth.");
                    transit = null;
                }
                int fromFile = 0;
                if (transit != null)
                {
                    var live = new HashSet<string>();
                    foreach (var t in transit.Transfers ?? new List<MpCargoTransferEntry>())
                    {
                        if (t == null || string.IsNullOrEmpty(t.TransferId)) continue;
                        live.Add(t.TransferId);
                        if (best.TryGetValue(t.TransferId, out var cur) && cur != null && cur.Seq >= t.Seq) continue;
                        best[t.TransferId] = t; fromFile++;
                    }
                    foreach (var id in new List<string>(best.Keys))
                        if (!live.Contains(id) && best[id].Seq > 0 && best[id].Seq <= transit.Seq) best.Remove(id);
                }
                int n = 0;
                foreach (var t in best.Values)
                {
                    if (string.IsNullOrEmpty(t?.TransferId)) continue;
                    List<CargoTransferItem>? items = null;
                    try { if (!string.IsNullOrEmpty(t.ItemsJson)) items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<CargoTransferItem>>(t.ItemsJson); } catch { }
                    if (items == null || items.Count == 0) continue;
                    _cargo[t.TransferId] = new HostCargo
                    {
                        TransferId = t.TransferId, PlanId = t.PlanId ?? "",
                        SourceKey = t.SourceAddressKey ?? "", DestKey = t.DestAddressKey ?? "",
                        SourcePid = t.SourcePid ?? "",
                        // r2 (F2/F3): "acked" is a REAL persisted stage now - the destination shelved the
                        // goods and the host is still offering the acknowledgement to the source's runner.
                        Stage = (t.Stage == "returning" || t.Stage == "acked") ? t.Stage : "delivering",
                        WithdrawAgain = t.WithdrawAgain,
                        Day = t.Day, Hour = t.Hour, Items = items, RelayedTo = "", SentCloseTo = "",
                        Seq = t.Seq,
                    };
                    if (t.Seq > _cargoSeq) _cargoSeq = t.Seq;
                    n++;
                }
                // Every live record leaves the seam with a stamp of its own, so the tombstone test above can
                // be trusted at the NEXT load, and the file is rewritten to match what was actually restored.
                foreach (var e in _cargo.Values) if (e != null && e.Seq == 0) e.Seq = ++_cargoSeq;
                // r4 (I3): from here the tail continues THIS save - whether the file was unioned or
                // discarded, it is rewritten from what was actually restored, under this manifest's stamp
                // and carrying the high-water above (r4 I1), and every later write keeps that stamp.
                MPSaveCoordinator.AdoptCargoTransitStamp(mine);
                if (n > 0 || hadFile) MPSaveCoordinator.PersistCargoTransitNow();
                if (n > 0) Plugin.Logger.LogInfo($"[Cargo] restored {n} in-transit cargo entr{(n == 1 ? "y" : "ies")} - the manifest and the transit file ({fromFile} newer in the file).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] manifest restore: {ex.Message}"); }
        }

        // === MERGER PHASE 4b (PEOPLE) P1 r2 (T2/T3): THE HIRE GATE AND THE OFFLINE ORIGIN ===

        private static readonly HashSet<string> _candidateHired = new();                                    // ids the host has already granted
        private static readonly Dictionary<string, List<CompanyCandidatesPayload>> _candidateHeld = new();  // offline pid -> notices

        /// <summary>HOST: the live read at the moment of commitment (review r1 MAJOR-3). An accept is
        /// granted only when the candidate has not been granted to anybody yet AND either the asker holds
        /// the claim or nobody does. The HIRED mark is what makes the hire safe even if a claim was
        /// wrongly freed by the stale sweep.</summary>
        private static void HostRouteCandidateAccept(CompanyCandidatesPayload p, string senderPid, string ownerPid)
        {
            string id = p.CandidateId ?? "";
            bool ok;
            string why = "";
            if (_candidateHired.Contains(id)) { ok = false; why = "already hired"; }
            else
            {
                string holder = _candidateClaims.TryGetValue(id, out var cl) ? (cl.pid ?? "") : "";
                if (holder.Length > 0 && holder != senderPid) { ok = false; why = $"'{holder}' holds the claim"; }
                else ok = true;
            }
            if (ok)
            {
                _candidateHired.Add(id);
                _candidateClaims[id] = (senderPid, UnityEngine.Time.unscaledTime);
                Plugin.Logger.LogInfo($"[Candidates] hire of '{id}' GRANTED to '{senderPid}'.");
            }
            else Plugin.Logger.LogWarning($"[Candidates] hire of '{id}' REFUSED ({why}).");
            var verdict = new CompanyCandidatesPayload { PlayerId = "host", Action = "hire-verdict", OwnerPid = ownerPid, CandidateId = id, ClaimedBy = senderPid, Ok = ok };
            if (senderPid == MPConfig.PlayerId) CompanyCandidates.Receive(verdict);
            else SendToPid(senderPid, MessageEnvelope.Create(MessageType.CompanyCandidates, "host", verdict));
            if (!ok) return;
            // The consumed mark goes to the WHOLE company, the origin included: every copy drops and the
            // origin's own record is discarded through the game's own path (held if they are offline).
            var mark = new CompanyCandidatesPayload { PlayerId = "host", Action = "hired", OwnerPid = ownerPid, CandidateId = id, ClaimedBy = senderPid, Ok = true };
            FanOutOrHoldCandidateMark(mark, ownerPid);
            if (_candidatesByOwner.TryGetValue(ownerPid, out var pool) && pool?.Candidates != null)
                pool.Candidates.RemoveAll(r => (r?.Staff?.Id ?? "") == id);
            Plugin.Logger.LogInfo($"[Candidates] HIRED mark fanned out for '{id}'.");
        }

        /// <summary>U3(b) r3: every pid the host knows in one company - the ONLINE members from the live
        /// group model, plus every pid that has published a pool, held a claim, or has notices waiting
        /// (those three tables outlive a disconnect; only LEAVING the company clears them, through
        /// HostForgetCandidatesOf). Membership for an OFFLINE pid is answered through the STABLE id, which
        /// is never pruned on departure - MergedRuntime could not answer for them at all.</summary>
        private static List<string> CompanyPidsFor(string anyPid)
        {
            var outp = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(anyPid)) return outp;
                string grp = MergerSync.GroupOfStable(StableOfPid(anyPid));
                if (string.IsNullOrEmpty(grp)) return outp;
                void Add(string pid)
                {
                    if (string.IsNullOrEmpty(pid) || outp.Contains(pid)) return;
                    if (MergerSync.GroupOfStable(StableOfPid(pid)) != grp) return;
                    outp.Add(pid);
                }
                foreach (var kv in _candidatesByOwner) Add(kv.Key);
                foreach (var kv in _candidateClaims) Add(kv.Value.pid ?? "");
                foreach (var kv in _candidateHeld) Add(kv.Key);
                foreach (var cp in ConnectedClientPeers()) Add(cp.playerId);
                Add(MPConfig.PlayerId);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Candidates] company roster of '{anyPid}': {ex.GetType().Name}: {ex.Message}"); }
            return outp;
        }

        /// <summary>U3(b) r3 (re-check MAJOR-3): the HIRED mark must reach EVERY member of the company, not
        /// only the origin and whoever happens to be online. An offline member's display copy is still in
        /// their save when they come back; with no notice to remove it - and with the fan-out reaching only
        /// online members before - they could press Accept on a negotiation they still had open and the
        /// game's own hire would mint a SECOND real employee with the hired person's id. Online members get
        /// it now; offline ones are HELD per pid (idempotent by action+id) and replayed at their next join.
        /// </summary>
        private static void FanOutOrHoldCandidateMark(CompanyCandidatesPayload mark, string ownerPid)
        {
            try
            {
                FanOutCandidates(mark, ownerPid, includeOwner: true);
                int held = 0;
                foreach (var pid in CompanyPidsFor(ownerPid))
                {
                    if (pid == MPConfig.PlayerId || IsOnlinePid(pid)) continue;
                    HostSendOrHoldCandidate(pid, mark);
                    held++;
                }
                if (held > 0)
                    Plugin.Logger.LogInfo($"[Candidates] the hired mark for '{mark.CandidateId}' is held for {held} offline member(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Candidates] hired-mark fan-out: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST: a notice the ORIGIN must see even if they are not here (review r1 MAJOR-4 - the
        /// MergerTax pay-all hold, mirrored). Held per pid, replayed at their next join.</summary>
        private static void HostSendOrHoldCandidate(string pid, CompanyCandidatesPayload pay)
        {
            if (string.IsNullOrEmpty(pid) || pay == null) return;
            if (pid == MPConfig.PlayerId) { CompanyCandidates.Receive(pay); return; }
            if (IsOnlinePid(pid)) { SendToPid(pid, MessageEnvelope.Create(MessageType.CompanyCandidates, "host", pay)); return; }
            if (!_candidateHeld.TryGetValue(pid, out var list)) { list = new List<CompanyCandidatesPayload>(); _candidateHeld[pid] = list; }
            foreach (var h in list)
                if (h != null && h.Action == pay.Action && h.CandidateId == pay.CandidateId) return;   // idempotent by (action, id)
            list.Add(pay);
            Plugin.Logger.LogInfo($"[Candidates] held {list.Count} notice(s) for offline '{pid}'.");
        }

        /// <summary>HOST: deliver what was held for a member who has just come back.</summary>
        private static void HostFlushHeldCandidates(MPLink peer, string pid)
        {
            try
            {
                if (peer == null || string.IsNullOrEmpty(pid)) return;
                if (!_candidateHeld.TryGetValue(pid, out var list) || list == null || list.Count == 0) return;
                foreach (var h in list)
                    if (h != null) Send(peer, MessageEnvelope.Create(MessageType.CompanyCandidates, "host", h));
                Plugin.Logger.LogInfo($"[Candidates] replayed {list.Count} notice(s) to '{pid}'.");
                _candidateHeld.Remove(pid);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Candidates] held replay: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>HOST: a member left the company (or the world) - their rows leave every other member's
        /// pool, their claims go back, and nothing of theirs is held any more (review r1 MINOR-9).</summary>
        public static void HostForgetCandidatesOf(string pid)
        {
            try
            {
                if (string.IsNullOrEmpty(pid)) return;
                bool had = _candidatesByOwner.Remove(pid);
                var drop = new List<string>();
                foreach (var kv in _candidateClaims) if ((kv.Value.pid ?? "") == pid) drop.Add(kv.Key);
                foreach (var cid in drop) _candidateClaims.Remove(cid);
                _candidateHeld.Remove(pid);
                if (had || drop.Count > 0)
                {
                    // An EMPTY pool for that owner is the message every copy needs: the apply's absolute-set
                    // rule then removes every row of theirs.
                    FanOutCandidates(new CompanyCandidatesPayload { PlayerId = "host", Action = "pool", OwnerPid = pid, Candidates = new List<CandidateRow>() }, pid, includeOwner: false);
                    Plugin.Logger.LogInfo($"[Candidates] '{pid}' left - their rows are dropped from every pool and {drop.Count} claim(s) released.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Candidates] forget '{pid}': {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Host: a new world, a load, or a dissolve - the session tables go (review r1 MINOR-9).</summary>
        public static void HostResetCandidates()
        {
            _candidatesByOwner.Clear(); _candidateClaims.Clear(); _candidateHired.Clear(); _candidateHeld.Clear();
        }

        /// <summary>Shared-shop slice 4: one item's price at a shared shop → the shop's OWNER, plus the per-sender
        /// rate cap. Merger phase 2 wave 1 (2026-09-11): the gate is the UNION check (GrantSync.IsGranted folds
        /// MergerSync.MergedRuntime in, as HostRouteEmployeeEdit already does), so a company member pricing a
        /// merger-flipped partner shop reaches the owner instead of editing their own replica.</summary>
        public static void HostRouteSharedPriceEdit(SharedPriceEditPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.ItemName) || string.IsNullOrEmpty(senderPid)) return;
                if (!SharedRateOk(senderPid, "price edit")) return;
                string ownerPid = SharedShopOwnerPid(p.AddressKey);
                if (ownerPid.Length == 0) { Plugin.Logger.LogWarning($"[SharedShop] price edit for unowned '{p.AddressKey}' from '{senderPid}' — dropped."); return; }
                if (ownerPid == senderPid) return;
                if (!GrantSync.IsGranted(GrantKind.Business, ownerPid, senderPid))
                { Plugin.Logger.LogWarning($"[SharedShop] price edit by '{senderPid}' on '{p.AddressKey}' (owner '{ownerPid}') — no Business permission and not a company member, dropped."); return; }
                string ptarget = RouteTargetFor(p.AddressKey, ownerPid);   // W3-0 retrofit: an offline owner no longer swallows the edit
                if (ptarget.Length == 0 || ptarget == senderPid) return;
                if (ptarget == MPConfig.PlayerId) SharedShopPrices.ApplyOnOwner(p);
                else SendToPid(ptarget, MessageEnvelope.Create(MessageType.SharedPriceEdit, "host", p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SharedShop] HostRouteSharedPriceEdit: {ex.Message}"); }
        }

        /// <summary>Shared-shop slice 4 (ruling 25): "request" → the shop's owner; "snapshot" → the ONE editor that
        /// asked (ToPid), never a broadcast. Both directions are grant-gated on the address's owner.</summary>
        public static void HostRouteSharedSalesHistory(SharedSalesHistoryPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(senderPid)) return;
                if (!SharedRateOk(senderPid, "sales history")) return;
                string ownerPid = SharedShopOwnerPid(p.AddressKey);
                if (ownerPid.Length == 0) return;
                if (p.Action == "request")
                {
                    if (ownerPid == senderPid) return;
                    if (!GrantSync.IsGranted(GrantKind.Business, ownerPid, senderPid))   // wave 2: UNION — direct grant or merger membership
                    { Plugin.Logger.LogWarning($"[SharedShop] sales-history request by '{senderPid}' on '{p.AddressKey}' — no Business permission and not a company member, dropped."); return; }
                    if (!AskOwnerOnline("sales-history", p.AddressKey, senderPid, ownerPid)) return;   // W3-0 r1 (F7)
                    if (ownerPid == MPConfig.PlayerId) SharedShopPrices.HandleSalesHistory(p);
                    else SendToPid(ownerPid, MessageEnvelope.Create(MessageType.SharedSalesHistory, "host", p));
                }
                else if (p.Action == "snapshot")
                {
                    if (senderPid != ownerPid) { Plugin.Logger.LogWarning($"[SharedShop] sales snapshot for '{p.AddressKey}' from non-owner '{senderPid}' — dropped."); return; }
                    if (string.IsNullOrEmpty(p.ToPid)) return;
                    if (!GrantSync.IsGranted(GrantKind.Business, ownerPid, p.ToPid)) return;   // wave 2: UNION
                    if (p.ToPid == MPConfig.PlayerId) SharedShopPrices.HandleSalesHistory(p);
                    else SendToPid(p.ToPid, MessageEnvelope.Create(MessageType.SharedSalesHistory, "host", p));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SharedShop] HostRouteSharedSalesHistory: {ex.Message}"); }
        }

        /// <summary>H-BIZ-1: "request" → the address's OWNER (answered here when the host owns it); "answer" → the ONE
        /// viewer that asked (ToPid), never a broadcast. No grant gate — the native page shows an estimate to anyone —
        /// but only the ledger owner may answer, and the snapshot rate bucket applies. An absent owner (ledger holds a
        /// stable id, no peer) still never answers — the viewer keeps $0 — but since W3-0 r1 (F7) the refusal says so
        /// in the log instead of dying inside SendToPid.</summary>
        public static void HostRouteShopValuation(ShopValuationPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(senderPid)) return;
                if (!SharedRateOk(senderPid, "valuation", snapshotBucket: p.Action == "answer")) return;   // review #7: viewer requests ride the edit bucket, owner answers the snapshot bucket
                string ownerPid = SharedShopOwnerPid(p.AddressKey);
                if (ownerPid.Length == 0) return;                                   // unowned / AI-run: the host-synced AI valuation covers it
                if (p.Action == "request")
                {
                    if (ownerPid == senderPid) return;
                    if (!AskOwnerOnline("valuation", p.AddressKey, senderPid, ownerPid)) return;   // W3-0 r1 (F7)
                    if (ownerPid == MPConfig.PlayerId) ShopValuation.Handle(p);
                    else SendToPid(ownerPid, MessageEnvelope.Create(MessageType.ShopValuation, "host", p));
                }
                else if (p.Action == "answer")
                {
                    if (senderPid != ownerPid) { Plugin.Logger.LogWarning($"[Valuation] answer for '{p.AddressKey}' from non-owner '{senderPid}' — dropped."); return; }
                    if (string.IsNullOrEmpty(p.ToPid)) return;
                    if (p.ToPid == MPConfig.PlayerId) ShopValuation.Handle(p);
                    else SendToPid(p.ToPid, MessageEnvelope.Create(MessageType.ShopValuation, "host", p));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Valuation] HostRouteShopValuation: {ex.Message}"); }
        }

        /// <summary>Shared-shop slice 6: "request" → the building's owner; "snapshot" → the ONE helper that
        /// asked (ToPid), never a broadcast. Both directions are grant-gated on the address's owner — the exact
        /// shape of the slice-4 sales-history route.</summary>
        public static void HostRouteSharedWorkInfo(SharedWorkInfoPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(senderPid)) return;
                // Review #4: BOTH directions ride the roomier snapshot bucket — a card round must never
                // starve the 10/s edit bucket that schedule/staff/price edits depend on.
                if (!SharedRateOk(senderPid, "work info", snapshotBucket: true)) return;
                string ownerPid = SharedShopOwnerPid(p.AddressKey);
                if (ownerPid.Length == 0) return;
                if (p.Action == "request")
                {
                    if (ownerPid == senderPid) return;
                    if (!GrantSync.IsGranted(GrantKind.Business, ownerPid, senderPid))   // wave 2: UNION — direct grant or merger membership
                    { Plugin.Logger.LogWarning($"[SharedShop] work-info request by '{senderPid}' on '{p.AddressKey}' — no Business permission and not a company member, dropped."); return; }
                    if (!SharedWorkAddressAllowed(p.AddressKey, MergerSync.MergedRuntime(ownerPid, senderPid)))
                    { Plugin.Logger.LogWarning($"[SharedShop] work-info request by '{senderPid}' for excluded '{p.AddressKey}' (empty premises / HQ) — dropped."); return; }
                    // MERGER PHASE 2 WAVE 4 r2 (D21): the SELL-ALL QUOTE is not a tab snapshot. It must be
                    // answered by the machine that HOLDS the stock (the owner, or its absence stand-in), not
                    // by the ledger owner, and like the three parity routes it is MEMBERSHIP-only - ruling 29
                    // keeps a permission helper away from the owner's inventory.
                    if (p.Tab == "sellquote")
                    {
                        if (!MergerSync.MergedRuntime(ownerPid, senderPid))
                        { Plugin.Logger.LogWarning($"[Merger] sell-all quote by '{senderPid}' on '{p.AddressKey}' REFUSED: not a company member with owner '{ownerPid}'."); return; }
                        string qtarget = RouteTargetFor(p.AddressKey, ownerPid);   // W3-0: owner online → owner, else its simulator
                        if (qtarget.Length == 0)
                        { Plugin.Logger.LogWarning($"[Merger] sell-all quote REFUSED for '{p.AddressKey}': nobody is running that building (RouteTargetFor)."); return; }
                        if (qtarget == senderPid) return;
                        Plugin.Logger.LogInfo($"[Merger] sell-all quote routed to '{qtarget}' for '{p.AddressKey}'");
                        if (qtarget == MPConfig.PlayerId) SharedShopWorkTabs.HandleWorkInfo(p);
                        else SendToPid(qtarget, MessageEnvelope.Create(MessageType.SharedWorkInfo, "host", p));
                        return;
                    }
                    if (!AskOwnerOnline("work-info", p.AddressKey, senderPid, ownerPid)) return;   // W3-0 r1 (F7)
                    if (ownerPid == MPConfig.PlayerId) SharedShopWorkTabs.HandleWorkInfo(p);
                    else SendToPid(ownerPid, MessageEnvelope.Create(MessageType.SharedWorkInfo, "host", p));
                }
                else if (p.Action == "snapshot")
                {
                    // W3-0 r1 (F3): the answer comes from whichever machine RUNS the address (owner online → owner,
                    // else its absence simulator) — the ledger owner alone dropped the simulator's echo and the
                    // REVERT a rejected edit depends on with it.
                    string wrunner = RouteTargetFor(p.AddressKey, ownerPid);
                    if (senderPid != wrunner)
                    { Plugin.Logger.LogWarning($"[SharedShop] work snapshot for '{p.AddressKey}' from '{senderPid}', which is not the machine running it ('{(wrunner.Length > 0 ? wrunner : "nobody")}'; ledger owner '{ownerPid}') — dropped."); return; }
                    if (string.IsNullOrEmpty(p.ToPid)) return;
                    if (!GrantSync.IsGranted(GrantKind.Business, ownerPid, p.ToPid)) return;   // wave 2: UNION
                    if (p.ToPid == MPConfig.PlayerId) SharedShopWorkTabs.HandleWorkInfo(p);
                    else SendToPid(p.ToPid, MessageEnvelope.Create(MessageType.SharedWorkInfo, "host", p));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SharedShop] HostRouteSharedWorkInfo: {ex.Message}"); }
        }

        /// <summary>4c part 2a (E4): (plan id|seq|op|sender) already routed. The host is the one place every
        /// member's edits meet, so this is where "the same edit twice is one edit" is decided. r2 MAJOR-2: it
        /// is cleared at the WORLD BOUNDARY with the rest of the host's per-session stores (ResetPaperwork) -
        /// a new world must not inherit the old world's seq numbers, and a sender's seq is seeded per session
        /// from the clock (CompanyPlans.NewSeqBase), so within one session it only climbs.</summary>
        private static readonly HashSet<string> _planEditSeen = new HashSet<string>(StringComparer.Ordinal);

        internal static void ResetPlanEditSeen() { lock (_planEditSeen) _planEditSeen.Clear(); }

        /// <summary>Shared-shop slice 6b/6c: a helper's warehouse/factory/marketing/settings edit → the
        /// building's owner (applied here if the host owns it). Rate-capped like every routed op, and since
        /// merger phase 2 wave 3 (W3-1) gated on the UNION check, so a company member's native work-tab edit
        /// on a merger-flipped partner building reaches the owner. Since phase 4c part 1 a HEADQUARTERS is
        /// admitted too, but only for a COMPANY MEMBER (SharedWorkAddressAllowed's `mergedHq`).</summary>
        public static void HostRouteSharedWorkEdit(SharedWorkEditPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(senderPid)) return;
                if (!SharedRateOk(senderPid, "work edit")) return;
                // 4c part 2a r2 MAJOR-6: THE REFUSAL ANSWER. The runner could not apply an edit and is telling
                // the ONE member that sent it (StationId). It is FORWARDED, never fanned out and never
                // re-routed at the headquarters - the address is the plan's, and the runner is the sender.
                if (p.Op == "mergerplanedit" && p.PlanOp == "refused")
                {
                    string back = p.StationId ?? "";
                    if (back.Length == 0 || back == senderPid) return;
                    if (!MergerSync.MergedRuntime(back, senderPid))
                    { Plugin.Logger.LogWarning($"[Merger] plan-edit refusal from '{senderPid}' for '{back}' dropped: they are not company members."); return; }
                    if (back == MPConfig.PlayerId) SharedShopWorkTabs.OwnerApplyEdit(p);
                    else SendToPid(back, MessageEnvelope.Create(MessageType.SharedWorkEdit, "host", p));
                    Plugin.Logger.LogInfo($"[Merger] plan-edit refusal forwarded to '{back}' from '{senderPid}' ({p.Family} plan {p.PlanId}).");
                    return;
                }
                string ownerPid = SharedShopOwnerPid(p.AddressKey);
                if (ownerPid.Length == 0 || ownerPid == senderPid) return;
                if (!GrantSync.IsGranted(GrantKind.Business, ownerPid, senderPid))   // wave 3 (W3-1): UNION — direct grant or merger membership
                { Plugin.Logger.LogWarning($"[SharedShop] work edit by '{senderPid}' on '{p.AddressKey}' — no Business permission and not a company member, dropped."); return; }
                // MERGER PHASE 2 WAVE 4 (V2): the three PARITY routes ride this envelope as new Ops (rule 5 -
                // one new MessageType in the whole wave, and it went to the display copies). They are gated on
                // MEMBERSHIP ONLY, never on a bare Business grant: a permission helper gets today's behaviour
                // exactly (ruling 29 keeps Sell-All refused for them). "mergerplan" ALONE skips
                // SharedWorkAddressAllowed, which excludes a HEADQUARTERS for the grant surface (rulings
                // 24/27) - a plan lives on one. The contract creation (a shop) and the sell-all (a warehouse)
                // keep the address check: neither has any business on an excluded address (r2 minor c - the
                // code used to skip it for all three while this comment already said otherwise).
                // PHASE 5 (D27/D29, 2026-09-12): the deed and identity commitments ride the same envelope
                // and the same MEMBERSHIP-ONLY gate — a permission helper never terminates a rental or shuts
                // a shop down (ruling 34), whatever grant they hold.
                bool w4 = p.Op == "mergercontract" || p.Op == "mergersellall" || p.Op == "mergerplan"
                       || p.Op == "mergerplanedit" || p.Op == "mergerterminate" || p.Op == "mergershutdown";
                if (w4 && !MergerSync.MergedRuntime(ownerPid, senderPid))
                { Plugin.Logger.LogWarning($"[Merger] {p.Op} by '{senderPid}' on '{p.AddressKey}' REFUSED: not a company member with owner '{ownerPid}'."); return; }
                // 4c part 2a (E4): the host SERIALISES plan edits per plan id. A second leg carrying a
                // (plan id, seq, op) already seen is a resend and is dropped here, so a member that pressed
                // twice - or reconnected and re-sent - cannot double an edit even if the runner changed.
                if (p.Op == "mergerplanedit")
                {
                    if (string.IsNullOrEmpty(p.PlanId) || string.IsNullOrEmpty(p.Family) || string.IsNullOrEmpty(p.PlanOp))
                    { Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED for '{p.AddressKey}': the leg named no family, plan or op."); return; }
                    string key = p.PlanId + "|" + p.EditSeq + "|" + p.PlanOp + "|" + senderPid;
                    // r3 G4a: the table is CLEARED under `lock (_planEditSeen)` (ResetPlanEditSeen, :7855), so
                    // the add and the cap take the same lock - an unlocked Add racing a Clear is the one way a
                    // HashSet corrupts and starts answering wrongly.
                    bool seen;
                    lock (_planEditSeen)
                    {
                        // The cap is applied BEFORE the add: clearing afterwards discarded the key that had
                        // just been recorded, so an immediate resend of that very edit would apply twice.
                        if (_planEditSeen.Count > 4000) _planEditSeen.Clear();   // a session-long cap, not a leak
                        seen = !_planEditSeen.Add(key);
                    }
                    if (seen)
                    { Plugin.Logger.LogInfo($"[Merger] plan edit {p.Family} {p.PlanOp} ({p.PlanId}, seq {p.EditSeq}) from '{senderPid}' already routed - dropped."); return; }
                }
                if (w4 && p.Op == "mergerplan")
                {
                    if (p.Plan == null) { Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED for '{p.AddressKey}': the payload carried no plan."); return; }
                    if (PlanCrossesCompanies(p.Plan, out var xwhy))
                    { Plugin.Logger.LogWarning($"[Merger] plan REFUSED cross-owner for '{p.AddressKey}' (plan {p.Plan.Id}): {xwhy} — an end outside the company that owns the plan."); return; }
                }
                // PHASE 5 r2 (J1): the two DEED/IDENTITY ops skip this exclusion exactly as "mergerplan" does.
                // SharedWorkAddressAllowed is a WORK-TAB predicate: it answers false for a WAREHOUSE (which
                // carries no businessTypeName), for empty premises and for a headquarters. Ending the rental
                // of an EMPTY building is the commonest terminate there is, and a warehouse is a business the
                // game lets its owner shut down, so both were being dropped here. These two act on the
                // TENANCY and on the BUSINESS itself, not on a work tab, and every other gate still holds:
                // the membership check above, RouteTargetFor below, and the runner's own refusals.
                bool skipAddrGate = p.Op == "mergerplan" || p.Op == "mergerterminate" || p.Op == "mergershutdown";
                if (!skipAddrGate && !SharedWorkAddressAllowed(p.AddressKey, MergerSync.MergedRuntime(ownerPid, senderPid)))
                { Plugin.Logger.LogWarning($"[SharedShop] work edit by '{senderPid}' for excluded '{p.AddressKey}' (empty premises / HQ) — dropped."); return; }
                string wtarget = RouteTargetFor(p.AddressKey, ownerPid);   // W3-0
                if (wtarget.Length == 0)
                {
                    if (w4) Plugin.Logger.LogWarning($"[Merger] {p.Op} REFUSED for '{p.AddressKey}': nobody is running that building (RouteTargetFor).");
                    return;
                }
                if (wtarget == senderPid) return;
                if (w4)
                {
                    if (p.Op == "mergerterminate")     Plugin.Logger.LogInfo($"[Merger] terminate-rental routed to '{wtarget}' for '{p.AddressKey}'");
                    else if (p.Op == "mergershutdown") Plugin.Logger.LogInfo($"[Merger] shutdown routed to '{wtarget}' for '{p.AddressKey}'");
                    else if (p.Op == "mergercontract") Plugin.Logger.LogInfo($"[Merger] contract create routed to '{wtarget}' for '{p.AddressKey}'");
                    else if (p.Op == "mergersellall")  Plugin.Logger.LogInfo($"[Merger] sell-all routed to '{wtarget}' for '{p.AddressKey}'");
                    else if (p.Op == "mergerplanedit") Plugin.Logger.LogInfo($"[Merger] plan edit routed to '{wtarget}' for '{p.AddressKey}' ({p.Family} {p.PlanOp}, plan {p.PlanId}, seq {p.EditSeq})");
                    else                               Plugin.Logger.LogInfo($"[Merger] plan edit routed to '{wtarget}' for '{p.AddressKey}' (plan {p.Plan?.Id})");
                }
                if (wtarget == MPConfig.PlayerId) SharedShopWorkTabs.OwnerApplyEdit(p);
                else SendToPid(wtarget, MessageEnvelope.Create(MessageType.SharedWorkEdit, "host", p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SharedShop] HostRouteSharedWorkEdit: {ex.Message}"); }
        }

        /// <summary>The merged-companies state broadcast: per group, online member pids (enforcement)
        /// + the full display roster from the store (offline members stay listed by name), PLUS the
        /// pending-offer table (r4) — the one authority every machine derives its merger chips from.</summary>
        private static MergerStatePayload BuildMergerState()
        {
            var pay = new MergerStatePayload();
            foreach (var grp in MergerSync.StoreGroups)
            {
                var info = new MergerGroupInfo { GroupId = grp.Key };
                // Phase 1-A (D3): walk the group in JOIN ORDER (founder first) — the display name, the
                // ordered rosters and FounderPid all read off this one walk.
                string founder = MergerSync.FounderOfGroup(grp.Key);
                var walk = MergerSync.JoinOrderOfGroup(grp.Key);
                if (string.IsNullOrEmpty(founder) || !grp.Value.Contains(founder))
                    founder = walk.Count > 0 ? walk[0] : "";
                foreach (var s in walk)
                {
                    info.MemberCount++;
                    string pid = PidOfStable(s);
                    // NOT filtered by IsOnlinePid (manager, r4 read): MemberPids feeds IsMemberPid/MergedRuntime on every
                    // machine - access to a member's shops, the rivals fold and the register/staff rules must keep
                    // naming a member who is merely OFFLINE (their buildings stay flipped through BuildingKeys).
                    // Only the OFFER machinery resolves online pids (PidsOfGroup/PidsOfTargetKey via IsOnlinePid).
                    // Names, not ids: an online member resolves through the character-name map (phase 1-A
                    // fix — MemberNames used to carry the PlayerId), an offline one through the store.
                    string nm = string.IsNullOrEmpty(pid) ? GrantSync.NameOf(s) : DisplayNameFor(pid);
                    if (string.IsNullOrEmpty(nm)) nm = "(offline member)";
                    if (!string.IsNullOrEmpty(pid)) { info.MemberPids.Add(pid); info.MemberPidsOrdered.Add(pid); }
                    info.MemberNames.Add(nm);
                    info.MemberNamesOrdered.Add(nm);
                    if (s == founder) info.FounderPid = pid;
                }
                // D3: the founder's name then the rest in join order. The game has no per-player company
                // name (BusinessName is per building), so this is built from character names.
                info.DisplayName = string.Join(" & ", info.MemberNamesOrdered);
                // Slice 3: every building OPERATED by a group member (rental ledger; a member's own
                // buildings are harmless in the list — the receiver's flip skips natively-rented regs).
                foreach (var kv in BuildingOwners)
                {
                    string stable = kv.Value == "host" ? MPConfig.StableId
                                  : StableIdByPlayer.TryGetValue(kv.Value, out var os) ? (os ?? "") : "";
                    if (!string.IsNullOrEmpty(stable) && grp.Value.Contains(stable))
                        info.BuildingKeys.Add(kv.Key);
                }
                pay.Groups.Add(info);
            }
            // r4: the offer table travels WITH the state, so every machine (the host included) derives its
            // own incoming row and "Cancel offer" chip from it instead of from optimistic UI writes and relays.
            foreach (var kv in _mergerPendingByTarget)
            {
                if (kv.Value == null) continue;
                var oi = new MergerOfferInfo { From = kv.Value.From, TargetKey = kv.Key, AskedPid = kv.Value.AskedPid };
                oi.TargetPids.AddRange(PidsOfTargetKey(kv.Key));   // ONLINE addressees only
                pay.Offers.Add(oi);
            }
            pay.Offers.Sort((x, y) => string.CompareOrdinal(x.TargetKey, y.TargetKey));   // r6: dictionary order is not stable across a remove+insert
            pay.Absences = MergerAbsence.HostSnapshot();   // P3-B (additive): who simulates what, for every machine
            return pay;
        }

        /// <summary>Slice 3: periodic merger-state refresh while any merger OR pending offer exists —
        /// newly rented/vacated company buildings reach every member's flip set without waiting for a
        /// grant or roster event (host flip Tick calls this ~10s). r4: also the broadcast every offer-table
        /// mutation ends with (force: true sends the emptying edge too — the last offer going away must
        /// reach the clients that are still rendering its chips). It has ALWAYS applied the state to the
        /// host itself before sending, exactly as RefreshGrantsAndBroadcast does.</summary>
        public static void RebroadcastMergerState(bool force = false)
        {
            if (!_running) return;
            if (!force && MergerSync.StoreGroups.Count == 0 && _mergerPendingByTarget.Count == 0) return;
            HostReconcileAbsence();   // P3-B: the 10 s cadence is also the re-designation safety net
            var pay = BuildMergerState();
            MergerSync.ApplyState(pay);
            Broadcast(MessageEnvelope.Create(MessageType.MergerState, "host", pay));
            BroadcastAllWalletGroups();   // slice 4: the wallet heartbeat rides the same 10s cadence
        }

        private static void HandleTaxiHail(MessageEnvelope env)
        {
            var payload = env.GetPayload<TaxiHailPayload>();
            if (payload == null || payload.TaxiIndex < 0) return;
            GameStatePatcher.EnqueueOnMainThread(() => TrafficSync.HostStopTaxi(payload.TaxiIndex));
        }

        /// <summary>Broadcasts the host's AI-traffic snapshot to all clients.</summary>
        /// <summary>T2: the traffic stream is PER-PEER now (culled + identity-gated in TrafficSync);
        /// peers come from ConnectedClientPeers (review MIN-9 — named peers only).</summary>
        /// <summary>TRAFFIC-SMOOTH S1 (2026-09-12): traffic snapshots — and ONLY those, lights stay
        /// reliable — leave the shared reliable-ordered FIFO. Returns true when the snapshot really went
        /// out unreliable (false = this transport fell back to reliable; LnlLink does that over MTU).</summary>
        // Review MINOR-5: this sits inside the 5 Hz traffic loop, so one broken link used to log a warning
        // per send. Cap the noise at 5 lines per session, then stay silent.
        private static int _trafficSendWarns;

        public static bool SendTrafficSnapshotTo(MPLink peer, TrafficSnapshotPayload payload)
        {
            if (!_running || peer == null || payload == null) return false;
            try { return peer.SendUnreliable(MessageEnvelope.Create(MessageType.TrafficSnapshot, "host", payload)); }
            catch (Exception ex)
            {
                if (_trafficSendWarns < 5)
                {
                    _trafficSendWarns++;
                    Plugin.Logger.LogWarning($"[Server] traffic snapshot → {peer.Describe}: {ex.Message}"
                                           + (_trafficSendWarns == 5 ? " (further traffic-send warnings suppressed)" : ""));
                }
                return false;
            }
        }

        /// <summary>TRAFFIC-APART P1/P2: the per-peer traffic MODE, RELIABLE and addressed to one peer. The
        /// snapshot lane above is unreliable on purpose (a lost pose costs nothing - the next one is 0.2 s away),
        /// but a lost mode message would leave that client running the wrong traffic until the host's 5 s
        /// re-assert, so this rides the reliable lane and the re-assert is the belt, not the braces.</summary>
        public static bool SendTrafficModeTo(MPLink peer, TrafficModePayload payload)
        {
            if (!_running || peer == null || payload == null) return false;
            try { peer.Send(MessageEnvelope.Create(MessageType.TrafficMode, "host", payload).Serialize(), reliable: true); return true; }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Server] traffic mode -> {peer.Describe}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Broadcasts the host's traffic-light states to all clients.</summary>
        public static void BroadcastTrafficLights(TrafficLightsPayload payload)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(MessageType.TrafficLights, "host", payload));
        }

        /// <summary>Broadcasts the host's parked-vehicle snapshot to all clients.</summary>
        // ── Business sync (Phase 1: exterior business state) ─────────────────

        /// <summary>Broadcast a single business-changed delta to every client that is APPLYING
        /// live traffic.  v9 latch: a loading peer DROPS these (MPClient early-return until settled)
        /// and is covered by the WorldReady delta re-send + join snapshot — field 2026-09-06: 664
        /// records were sent to a loading client and 8 applied.  Mirrors Broadcast per peer:
        /// serialize once, then peer.Send(bytes, reliable: true).</summary>
        public static void BroadcastBusinessChange(BusinessInfo info)
        {
            if (!_running || _transport == null) return;
            var payload = new BusinessChangePayload { Info = info };
            var bytes = MessageEnvelope.Create(MessageType.BusinessChange, "host", payload).Serialize();
            foreach (var cp in ConnectedClientPeers())
                if (IsPlayerApplying(cp.playerId)) cp.peer.Send(bytes, reliable: true);
        }

        /// <summary>Burst fix 2026-09-10: several changed business records in ONE envelope so the
        /// 4 KB deflate floor (Protocol.CompressOver) can apply — single BusinessChange records are
        /// 1.3-2.4 KB and never compress.  Same applying-peer rule as BroadcastBusinessChange; an
        /// empty list is a no-op.</summary>
        public static void BroadcastBusinessChangeBatch(List<BusinessInfo> infos)
        {
            if (!_running || _transport == null) return;
            if (infos == null || infos.Count == 0) return;
            var payload = new BusinessChangeBatchPayload { Infos = infos };
            var bytes = MessageEnvelope.Create(MessageType.BusinessChangeBatch, "host", payload).Serialize();
            foreach (var cp in ConnectedClientPeers())
                if (IsPlayerApplying(cp.playerId)) cp.peer.Send(bytes, reliable: true);
        }

        // Wave-2 (audit join item — MEASURED: the ~850 KB table travelled TWICE per join): the
        // WorldReady re-send only fires when the table actually changed during the load window.
        private static readonly Dictionary<int, long> _lastBizSnapSig = new();
        private static long BizSnapSig(byte[] d)
        {
            // Review M4 (MEASURED): stride-sampling missed 98-99.7% of SAME-LENGTH changes — and the
            // table is full of them (OwnerOpenState 1↔2, SignType, same-length renames). Full-byte
            // FNV-1a runs in well under a millisecond against a snapshot that costs tens of ms to build.
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < d.Length; i++) h = (h ^ d[i]) * 1099511628211UL;
                return (long)h;
            }
        }

        /// <summary>v9 (T6): per-peer map of AddressKey → per-business FNV sig, captured at every
        /// FULL snapshot send to that peer. The WorldReady resend compares against this and ships
        /// only the businesses that actually changed since — the whole-table sig almost never
        /// matched (joining itself always touches a few entries), so the "dedup" re-sent ~0.8 MB
        /// per join. Keyed by recycled peer id → pruned at disconnect like _lastBizSnapSig.</summary>
        private static readonly Dictionary<int, Dictionary<string, long>> _joinBizSigs = new();

        private static Dictionary<string, long> PerBusinessSigs(BusinessSnapshotPayload snap)
        {
            var map = new Dictionary<string, long>(snap.Businesses.Count);
            foreach (var b in snap.Businesses)
            {
                if (b == null || string.IsNullOrEmpty(b.AddressKey)) continue;
                map[b.AddressKey] = BizSnapSig(System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(b)));
            }
            return map;
        }

        /// <summary>Review MIN-4: the delta USED to ship each BusinessChange (~1.3 KB) uncompressed
        /// (below the 4 KB deflate floor) while the full table deflates to ~825 KB — the delta stays the
        /// cheaper form until several hundred entries. Field 2026-08-24 (P-JOINDELTA): a fresh
        /// host load's join window really does churn ~109 businesses, so the original cap of 80
        /// forced the full-book fallback on exactly the case the delta was built for; 200 keeps
        /// a wide margin over measured churn while still bounding a pathological burst.  Burst fix
        /// 2026-09-10: the delta now travels as ONE BusinessChangeBatch envelope, so it deflates
        /// like the table does instead of shipping N uncompressed records.</summary>
        private const int BizDeltaCap = 200;

        /// <summary>v9: send this peer only the businesses whose per-business sig differs from
        /// its stored baseline; fall back to the full table when a business VANISHED (a delta
        /// can't express removal), no baseline exists, or the delta exceeds BizDeltaCap.
        /// Updates the peer's baseline either way (freshSigs is shared read-only across peers —
        /// replaced wholesale, never mutated). Returns a log fragment. MAIN THREAD.</summary>
        private static string SendBusinessDeltaTo(MPLink peer, BusinessSnapshotPayload bsnap, Dictionary<string, long> freshSigs)
        {
            bool haveBase = _joinBizSigs.TryGetValue(peer.Id, out var baseline);
            bool removedSince = false;
            var changed = new List<BusinessInfo>();
            if (haveBase)
            {
                removedSince = baseline.Keys.Any(k => !freshSigs.ContainsKey(k));
                if (!removedSince)
                    foreach (var b in bsnap.Businesses)
                    {
                        if (b == null || string.IsNullOrEmpty(b.AddressKey)) continue;
                        if (baseline.TryGetValue(b.AddressKey, out var oldSig)
                            && freshSigs.TryGetValue(b.AddressKey, out var newSig) && oldSig == newSig) continue;
                        changed.Add(b);
                    }
            }
            if (haveBase && !removedSince && changed.Count <= BizDeltaCap)
            {
                if (changed.Count > 0)
                    peer.Send(MessageEnvelope.Create(MessageType.BusinessChangeBatch, "host",
                              new BusinessChangeBatchPayload { Infos = changed }).Serialize(), reliable: true);
                _joinBizSigs[peer.Id] = freshSigs;
                return changed.Count == 0 ? "business table UNCHANGED — skipped"
                                          : changed.Count + " changed business delta(s) in ONE batch (v9 — was a full table)";
            }
            var benv  = MessageEnvelope.Create(MessageType.BusinessSnapshot, "host", bsnap);
            var bdata = benv.Serialize();
            _lastBizSnapSig[peer.Id] = BizSnapSig(bdata);
            _joinBizSigs[peer.Id]    = freshSigs;
            peer.Send(bdata, reliable: true);
            // PROBE-START: P-JOINDELTA — field run 2026-08-24 hit "delta over the cap" with an
            // unknown changed-count (>80 of 826 businesses "changed" within ~2 min of a join is
            // suspicious: real churn, or a sig-unstable field drifting the whole table?). Name
            // the count and three examples so the next run answers it.
            string probeDetail = "";
            try
            {
                if (removedSince && baseline != null)
                    probeDetail = "; vanished e.g. " + string.Join(", ", baseline.Keys.Where(k => !freshSigs.ContainsKey(k)).Take(3));
                else if (changed.Count > 0)
                    probeDetail = $"; changed={changed.Count} e.g. " + string.Join(", ", changed.Take(3).Select(b => b.AddressKey));
            }
            catch { }
            // PROBE-END: P-JOINDELTA
            return bsnap.Businesses.Count + " businesses (full — "
                   + (removedSince ? "a business vanished" : !haveBase ? "no baseline" : "delta over the cap") + probeDetail + ")";
        }

        /// <summary>v9 review MAJOR-1: the daily full-table broadcast this release removed was
        /// also the only recurring drift heal — a client that silently missed one BusinessChange
        /// stayed wrong until the audit tripped. This is its change-bounded replacement: once
        /// per in-game day (the for-sale flip), diff each applying client's baseline and re-send
        /// only the drifted businesses. MAIN THREAD.</summary>
        public static void HealBusinessDeltas()
        {
            if (!_running) return;
            try
            {
                var snap = BusinessSync.BuildFullSnapshot();
                var sigs = PerBusinessSigs(snap);
                foreach (var (peer, pid) in ConnectedClientPeers())
                {
                    if (!IsPlayerApplying(pid)) continue;   // a loading client is covered by the join/resend path
                    string line = SendBusinessDeltaTo(peer, snap, sigs);
                    if (!line.StartsWith("business table UNCHANGED"))
                        Plugin.Logger.LogInfo($"[Server] daily business heal → '{pid}': {line}");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] HealBusinessDeltas: {ex.Message}"); }
        }

        /// <summary>Send the full business table to a single peer (on connect).</summary>
        public static void SendBusinessSnapshotTo(MPLink peer)
        {
            if (peer == null) return;
            try
            {
                long t0 = MPLoadProfiler.NowMs;
                var snap = BusinessSync.BuildFullSnapshot();
                MPLoadProfiler.Span($"HOST BuildFullSnapshot ({snap.Businesses.Count} buildings, {snap.BuildingsForSale.Count} for-sale)", t0);
                var env = MessageEnvelope.Create(MessageType.BusinessSnapshot, "host", snap);
                var data = env.Serialize();
                int bytes = data.Length;
                _lastBizSnapSig[peer.Id] = BizSnapSig(data);
                _joinBizSigs[peer.Id]    = PerBusinessSigs(snap);   // v9: baseline for the WorldReady delta resend
                peer.Send(data, reliable: true);
                MPLoadProfiler.Mark($"HOST sent BusinessSnapshot to '{peer.Id}': {bytes} bytes ({snap.Businesses.Count} buildings)");
                Plugin.Logger.LogInfo($"[Server] Sent business snapshot to '{peer.Id}': {snap.Businesses.Count} buildings, {snap.BuildingsForSale.Count} for-sale, {bytes}B on the wire (deflated — SizeWatch reports the raw size).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendBusinessSnapshotTo: {ex.Message}"); }
        }

        // ── Interior sync (Phase 2) ──────────────────────────────────────────

        /// <summary>Send a single building's interior snapshot to one peer (initial response to InteriorRequest).</summary>
        /// <summary>Send the full passenger lock + seat state to a single peer (join replay).</summary>
        public static void SendPassengerSnapshotTo(MPLink peer)
        {
            if (peer == null) return;
            try
            {
                Send(peer, MessageEnvelope.Create(MessageType.PassengerSnapshot, "host", PassengerSync.BuildSnapshot()));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendPassengerSnapshotTo: {ex.Message}"); }
        }

        // Review r2 M1: SendPermissionSnapshotTo was DELETED here - it had no call site anywhere in the
        // mod, so everything inside it (including phase 4a's books replay) was dead code. Its other three
        // sends are all covered by paths that do run: RefreshGrantsAndBroadcast (called from inside
        // SendJoinReplayTo) broadcasts MergerState and the PermissionSnapshot, and the per-group wallet
        // state rides RebroadcastMergerState -> BroadcastAllWalletGroups on the host's 10 s cadence.

        // MERGER PHASE 4a - COMPANY BOOKS (2026-09-11, D14/D18/D19)

        /// <summary>HOST: fan ONE owner's books out to every ONLINE co-member of that owner's company
        /// (never back to the owner - its own books must not be overlaid twice). Returns how many
        /// machines actually got them; the host itself counts when it is a co-member.</summary>
        public static int SendCompanyBooksToGroup(CompanyBooksPayload p)
        {
            int fanout = 0;
            try
            {
                if (!_running || p == null || string.IsNullOrEmpty(p.OwnerPid)) return 0;
                if (!MergerSync.InAnyGroup(p.OwnerPid)) { Plugin.Logger.LogInfo($"[Books] fan-out refused: '{p.OwnerPid}' is not in a company."); return 0; }
                byte[]? bytes = null;
                foreach (var cp in ConnectedClientPeers())
                {
                    if (cp.playerId == p.OwnerPid) continue;
                    if (!MergerSync.MergedRuntime(p.OwnerPid, cp.playerId)) continue;
                    bytes ??= MessageEnvelope.Create(MessageType.CompanyBooks, "host", p).Serialize();
                    cp.peer.Send(bytes, reliable: true);
                    fanout++;
                }
                if (MPConfig.PlayerId != p.OwnerPid && MergerSync.MergedRuntime(p.OwnerPid, MPConfig.PlayerId))
                { CompanyBooks.Receive(p); fanout++; }        // the host is a member too
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] fan-out: {ex.Message}"); }
            return fanout;
        }

        // ── MERGER PHASE 2 WAVE 4 - COMPANY LISTS (display copies, D18) ──────────

        /// <summary>Owner stable ids whose stored entry the last HostFileSimulatedPaperwork changed.</summary>
        private static readonly List<string> _lastFiledOwners = new();

        /// <summary>HOST: ship ONE owner's agreement + headquarters-plan families to every ONLINE co-member of that owner
        /// (never back to the owner - its own lists are its own save). `stable` is the PaperworkStore key.
        /// Refusals are logged, never silent.</summary>
        public static int FanOutCompanyLists(string stable, string why)
        {
            int fanout = 0;
            try
            {
                if (!_running || string.IsNullOrEmpty(stable)) return 0;
                string ownerPid = PidOfStable(stable);
                if (string.IsNullOrEmpty(ownerPid))
                { Plugin.Logger.LogInfo($"[CompanyLists] fan-out refused for stable '{stable}' ({why}): no player id known this session."); return 0; }
                if (!MergerSync.InAnyGroup(ownerPid))
                { Plugin.Logger.LogInfo($"[CompanyLists] fan-out refused: '{ownerPid}' is not in a company."); return 0; }
                var pay = BuildCompanyLists(stable, ownerPid);
                if (pay == null) return 0;
                byte[]? bytes = null;
                foreach (var cp in ConnectedClientPeers())
                {
                    if (cp.playerId == ownerPid) continue;
                    if (!MergerSync.MergedRuntime(ownerPid, cp.playerId)) continue;
                    bytes ??= MessageEnvelope.Create(MessageType.CompanyLists, "host", pay).Serialize();
                    cp.peer.Send(bytes, reliable: true);
                    fanout++;
                }
                if (MPConfig.PlayerId != ownerPid && MergerSync.MergedRuntime(ownerPid, MPConfig.PlayerId))
                { GameStatePatcher.EnqueueOnMainThread(() => CompanyLists.Receive(pay)); fanout++; }   // the host is a member too
                if (fanout > 0)
                    Plugin.Logger.LogInfo($"[CompanyLists] fanned out {pay.DeliveryContracts.Count} contracts, "
                                        + $"{pay.LogisticsManagerPlans.Count} logistics, {pay.PricingManagerPlans.Count} pricing, "
                                        + $"{pay.ImportPartnerships.Count} purchasing, {pay.HrManagerPlans.Count} hr, "
                                        + $"{pay.HeadhunterPlans.Count} headhunter plan(s) of '{ownerPid}' to {fanout} co-member(s) ({why}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] fan-out: {ex.Message}"); }
            return fanout;
        }

        private static CompanyListsPayload? BuildCompanyLists(string stable, string ownerPid)
        {
            string had;
            lock (_paperwork) had = _paperwork.TryGetValue(stable, out var pe) ? (pe.Json ?? "") : "";
            if (string.IsNullOrEmpty(had)) return null;
            BusinessPaperworkPayload? pw = null;
            try { pw = Newtonsoft.Json.JsonConvert.DeserializeObject<BusinessPaperworkPayload>(had); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] read of '{ownerPid}' entry: {ex.Message}"); return null; }
            if (pw == null) return null;
            return CompanyLists.Extract(pw, ownerPid);
        }

        /// <summary>HOST: a JOINER's catch-up - every co-member's agreement lists as they stand now. Runs
        /// from the same join replay the books and the feed ride (review r2 M1's call site).</summary>
        public static void SendCompanyListsTo(MPLink peer, string joinerPid)
        {
            try
            {
                if (string.IsNullOrEmpty(joinerPid)) return;
                int n = 0;
                List<string> keys;
                lock (_paperwork) keys = new List<string>(_paperwork.Keys);
                foreach (var stable in keys)
                {
                    string ownerPid = PidOfStable(stable);
                    if (string.IsNullOrEmpty(ownerPid) || ownerPid == joinerPid) continue;
                    if (!MergerSync.MergedRuntime(ownerPid, joinerPid)) continue;
                    var pay = BuildCompanyLists(stable, ownerPid);
                    if (pay == null) continue;
                    Send(peer, MessageEnvelope.Create(MessageType.CompanyLists, "host", pay));
                    n++;
                }
                if (n > 0) Plugin.Logger.LogInfo($"[CompanyLists] join replay to '{joinerPid}': {n} owner(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] join replay: {ex.Message}"); }
        }

        /// <summary>HOST: does this plan reach OUTSIDE ONE COMPANY? Its warehouse/factory end and every
        /// destination must belong to one member, or to CO-MEMBERS of one merged company - the same rule
        /// CargoEndsAreOneCompany applies to a single leg. PHASE 5 r2 (J2): a MIXED-OWNER plan is no longer
        /// refused. Since 4c part 2b the leg gate hands a mixed-owner leg to the routed cargo transfer at
        /// DELIVERY time, so the plan that creates it may be written; what stays refused is an end owned by
        /// somebody who is NOT in the company. An end the rental ledger does not know is skipped, as it
        /// always was: it is nobody's rented building, so there is nothing there to reach.</summary>
        private static bool PlanCrossesCompanies(PwLogisticsPlan plan, out string why)
        {
            why = "";
            if (plan == null) return false;
            string first = "", firstKey = "";
            foreach (var key in PlanEndKeys(plan))
            {
                if (!BuildingOwners.TryGetValue(key, out var o) || string.IsNullOrEmpty(o)) continue;
                string pid = o == "host" ? MPConfig.PlayerId : o;
                if (first.Length == 0) { first = pid; firstKey = key; continue; }
                if (pid != first && !MergerSync.MergedRuntime(pid, first))
                { why = $"'{firstKey}' is run by '{first}' and '{key}' by '{pid}', who are not in one company"; return true; }
            }
            return false;
        }

        private static IEnumerable<string> PlanEndKeys(PwLogisticsPlan plan)
        {
            if (!string.IsNullOrEmpty(plan.TargetAddressKey)) yield return plan.TargetAddressKey;
            foreach (var d in plan.Destinations ?? new List<PwLogisticsDestination>())
                if (!string.IsNullOrEmpty(d?.DeliveryTargetAddressKey)) yield return d.DeliveryTargetAddressKey;
        }

        /// <summary>HOST: one MergerTax envelope to one named member. FALSE = that member is not
        /// online here (the caller holds the pay-all for them).</summary>
        public static bool SendMergerTaxTo(string pid, MergerTaxPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(pid)) return false;
                if (pid == MPConfig.PlayerId)
                {
                    if (p.Action == "payown") { GameStatePatcher.EnqueueOnMainThread(() => CompanyBooks.PayOwnForCompany(p.PayerPid)); return true; }
                    if (p.Action == "report") { CompanyBooks.LogReport(p, p.PlayerId); return true; }
                    return false;
                }
                if (!_running) return false;
                foreach (var cp in ConnectedClientPeers())
                {
                    if (cp.playerId != pid) continue;
                    cp.peer.Send(MessageEnvelope.Create(MessageType.MergerTax, "host", p).Serialize(), reliable: true);
                    return true;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] send to '{pid}': {ex.Message}"); }
            return false;
        }

        /// <summary>HOST: replay every co-member's stored books to a JOINER, and deliver any tax
        /// pay-all that was held while they were offline (B9-iv).</summary>
        public static void SendCompanyBooksTo(MPLink peer, string joinerPid)
        {
            if (peer == null || string.IsNullOrEmpty(joinerPid)) return;
            try
            {
                int n = 0;
                foreach (var p in CompanyBooks.HostReplayFor(joinerPid))
                { Send(peer, MessageEnvelope.Create(MessageType.CompanyBooks, "host", p)); n++; }
                if (n > 0) Plugin.Logger.LogInfo($"[Books] replayed {n} member bundle(s) to '{joinerPid}' (join replay).");
                else Plugin.Logger.LogInfo($"[Books] join replay for '{joinerPid}': the host holds no co-member books yet.");
                CompanyBooks.HostFlushHeld(joinerPid);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] join replay: {ex.Message}"); }
        }

        // MERGER PHASE 4b - SHARED TRANSACTION FEED (2026-09-11, D19-5)

        /// <summary>HOST: relay ONE member's transaction record to every ONLINE co-member of that
        /// member's company (never back to the owner - its own entry is already in its own queue).</summary>
        public static int SendCompanyFeedToGroup(PwTransaction rec)
        {
            int fanout = 0;
            try
            {
                if (!_running || rec == null || string.IsNullOrEmpty(rec.OwnerPid)) return 0;
                if (!MergerSync.InAnyGroup(rec.OwnerPid)) { Plugin.Logger.LogInfo($"[Feed] relay refused: '{rec.OwnerPid}' is not in a company."); return 0; }
                var p = new CompanyFeedPayload { PlayerId = "host", Action = "entry" };
                p.Entries.Add(rec);
                byte[]? bytes = null;
                foreach (var cp in ConnectedClientPeers())
                {
                    if (cp.playerId == rec.OwnerPid) continue;
                    if (!MergerSync.MergedRuntime(rec.OwnerPid, cp.playerId)) continue;
                    bytes ??= MessageEnvelope.Create(MessageType.CompanyFeed, "host", p).Serialize();
                    cp.peer.Send(bytes, reliable: true);
                    fanout++;
                }
                if (MPConfig.PlayerId != rec.OwnerPid && MergerSync.MergedRuntime(rec.OwnerPid, MPConfig.PlayerId))
                { CompanyFeed.Receive(p); fanout++; }        // the host is a member too
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] relay: {ex.Message}"); }
            return fanout;
        }

        /// <summary>HOST: replay the co-members' recent entries to a JOINER. Memory only - the ring
        /// never rides the manifest (a member's own transactions are in its own save).</summary>
        public static void SendCompanyFeedTo(MPLink peer, string joinerPid)
        {
            if (peer == null || string.IsNullOrEmpty(joinerPid)) return;
            try
            {
                var entries = CompanyFeed.HostReplayFor(joinerPid);
                if (entries.Count == 0) { Plugin.Logger.LogInfo($"[Feed] join replay for '{joinerPid}': the host holds no co-member entries yet."); return; }
                var p = new CompanyFeedPayload { PlayerId = "host", Action = "replay" };
                p.Entries.AddRange(entries);
                Send(peer, MessageEnvelope.Create(MessageType.CompanyFeed, "host", p));
                Plugin.Logger.LogInfo($"[Feed] replayed {entries.Count} entries to '{joinerPid}' (join replay).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] join replay: {ex.Message}"); }
        }

        public static void SendInteriorSnapshotTo(MPLink peer, InteriorSnapshotPayload snap)
        {
            if (!_running || peer == null || snap == null) return;
            try
            {
                Send(peer, MessageEnvelope.Create(MessageType.InteriorSnapshot, "host", snap));
                Plugin.Logger.LogInfo($"[Server] Sent interior snapshot to peer={peer.Id} addr='{snap.AddressKey}': {InteriorSync.SnapshotSummary(snap)}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendInteriorSnapshotTo: {ex.Message}"); }
        }

        /// <summary>Broadcast an interior snapshot to a specific set of peer ids (the building's subscribers).</summary>
        public static void BroadcastInteriorSnapshotTo(System.Collections.Generic.HashSet<int> peerIds, InteriorSnapshotPayload snap)
        {
            if (!_running || peerIds == null || peerIds.Count == 0 || snap == null) return;
            try
            {
                var env = MessageEnvelope.Create(MessageType.InteriorSnapshot, "host", snap);
                byte[] data = env.Serialize();
                int sent = 0;
                foreach (var peer in _clients.Keys)
                {
                    if (peer == null) continue;
                    if (!peerIds.Contains(peer.Id)) continue;
                    peer.Send(data, reliable: true);
                    sent++;
                }
                if (sent > 0)
                    // Round-280 (S5): this is a FULL snapshot — no diff message exists; the old
                    // "diff broadcast" wording sent a field investigation down the wrong path.
                    Plugin.Logger.LogInfo($"[Server] Interior snapshot broadcast (full state) to {sent} subscriber(s) for '{snap.AddressKey}': {InteriorSync.SnapshotSummary(snap)}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastInteriorSnapshotTo: {ex.Message}"); }
        }

        /// <summary>v10 (T7/ruling 33): dirt values to exactly the players inside the building.</summary>
        public static void BroadcastInteriorDirtSyncTo(System.Collections.Generic.HashSet<int> peerIds, InteriorDirtSyncPayload dirt)
        {
            if (!_running || peerIds == null || peerIds.Count == 0 || dirt == null) return;
            try
            {
                var env = MessageEnvelope.Create(MessageType.InteriorDirtSync, "host", dirt);
                byte[] data = env.Serialize();
                foreach (var peer in _clients.Keys)
                {
                    if (peer == null || !peerIds.Contains(peer.Id)) continue;
                    peer.Send(data, reliable: true);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastInteriorDirtSyncTo: {ex.Message}"); }
        }

        /// <summary>v10 (T7): host → owner "push me the full interior" — sent when an owner's
        /// cargo-only upload can't be grafted (no cache / structure mismatch).</summary>
        public static void RequestOwnerInteriorResync(MPLink peer, string addressKey)
        {
            if (!_running || peer == null || string.IsNullOrEmpty(addressKey)) return;
            try { peer.Send(MessageEnvelope.Create(MessageType.InteriorRequest, "host", new InteriorRequestPayload { AddressKey = addressKey })); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] RequestOwnerInteriorResync: {ex.Message}"); }
        }

        /// <summary>Stage 0 (interior-edit design 2026-08): the owed-resync drain resolves the owner's
        /// peer LIVE at drain time (live reads at commitment — a peer captured at note time could be a
        /// reconnected player's dead link).  False = not connected; the caller drops the owed entry
        /// (the owner's next push or the entry serve heals).</summary>
        public static bool RequestOwnerInteriorResyncByPid(string ownerPid, string addressKey)
        {
            if (!_running || string.IsNullOrEmpty(ownerPid) || string.IsNullOrEmpty(addressKey)) return false;
            var peer = PeerForPid(ownerPid);
            if (peer == null) return false;
            RequestOwnerInteriorResync(peer, addressKey);
            return true;
        }

        /// <summary>STAGE 1b: send one edit delta to a building's subscribers, excluding the peer who
        /// SENT the edit (their machine made it natively; echoing it back is pure cost and the echo's
        /// ops would ser-match into no-ops anyway). One serialization, reliable lane.
        /// Review MINOR-Q: unlike BroadcastInteriorCargoSyncTo there is deliberately NO per-peer
        /// capability filter — protocol v13 refuses mixed sessions at the handshake, so every
        /// connected peer can parse type 194 by construction.</summary>
        public static void BroadcastInteriorDeltaTo(System.Collections.Generic.HashSet<int> peerIds, string excludePid, InteriorEditDeltaPayload p)
        {
            if (!_running || peerIds == null || peerIds.Count == 0 || p == null) return;
            try
            {
                int excludeId = -1;
                try { var xp = PeerForPid(excludePid); if (xp != null) excludeId = xp.Id; } catch { }
                var env = MessageEnvelope.Create(MessageType.BuildingInteriorDelta, "host", p);
                byte[] data = env.Serialize();
                foreach (var peer in _clients.Keys)
                {
                    if (peer == null || !peerIds.Contains(peer.Id) || peer.Id == excludeId) continue;
                    peer.Send(data, reliable: true);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastInteriorDeltaTo: {ex.Message}"); }
        }

        /// <summary>Round-281: broadcast one building's CARGO state to its subscribers.  Same shape as
        /// BroadcastInteriorSnapshotTo (one serialization, reliable send to the subscriber peer ids) —
        /// only the payload is the small absolute cargo message instead of the whole interior.  The
        /// caller has already established that EVERY peer in this set is delta-capable
        /// (InteriorSync's send gate); this method does not re-check, so it must never be called with
        /// an unfiltered set.  Returns the serialized size so the sender can report the saving.</summary>
        public static int BroadcastInteriorCargoSyncTo(System.Collections.Generic.HashSet<int> peerIds, InteriorCargoSyncPayload cargo)
        {
            if (!_running || peerIds == null || peerIds.Count == 0 || cargo == null) return 0;
            try
            {
                var env = MessageEnvelope.Create(MessageType.InteriorCargoSync, "host", cargo);
                byte[] data = env.Serialize();
                int sent = 0;
                foreach (var peer in _clients.Keys)
                {
                    if (peer == null) continue;
                    if (!peerIds.Contains(peer.Id)) continue;
                    peer.Send(data, reliable: true);
                    sent++;
                }
                return sent > 0 ? data.Length : 0;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastInteriorCargoSyncTo: {ex.Message}"); return 0; }
        }

        // ── Player profile (Wave 5) ──────────────────────────────────────────

        /// <summary>
        /// Resolve a player's display name (in-character).  Falls back to the
        /// PlayerId if the character name isn't known yet (e.g. before the
        /// player has finished character creation or sent their profile).
        /// </summary>
        public static string DisplayNameFor(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return "";
            if (_characterNamesByPlayerId.TryGetValue(playerId, out var n) && !string.IsNullOrWhiteSpace(n))
                return n;
            return playerId;
        }

        /// <summary>
        /// Host-side equivalent of MPClient.SendPlayerProfile.  Reads the
        /// host's own CharacterData.name, writes it into our dict, and
        /// broadcasts to all peers so their UIs use the in-character name.
        /// Called from ReleaseStartupHold once everyone's in-game.
        /// </summary>
        public static void BroadcastHostProfile()
        {
            try
            {
                string name = MPConfig.PlayerId;
                var gi = SaveGameManager.Current;
                if (gi != null && gi.charactersData != null && gi.charactersData.Count > 0)
                {
                    var cd = gi.charactersData[0];
                    var cn = cd?.name?.ToString();
                    if (!string.IsNullOrWhiteSpace(cn)) name = cn;
                }
                _characterNamesByPlayerId[MPConfig.PlayerId] = name;
                // Also seed the host's local UI lookup dict so when host's own
                // UI calls GetRivalName(MPConfig.PlayerId), our Prefix returns
                // the character name (used by leaderboard / popups on host).
                GameStatePatcher.ClientRivalNames[MPConfig.PlayerId] = name;
                string portrait = ""; try { portrait = GameStatePatcher.ReadLocalPortraitBase64(); } catch { }
                int age = 0; try { age = GameStatePatcher.LocalAgeInYears(); } catch { }
                int gender = -1; try { if (gi?.charactersData != null && gi.charactersData.Count > 0) gender = (int)gi.charactersData[0].gender; } catch { }
                var p = new PlayerProfilePayload { PlayerId = MPConfig.PlayerId, CharacterName = name, PortraitPngBase64 = portrait, AgeInYears = age, Gender = gender };
                if (!string.IsNullOrEmpty(portrait)) GameStatePatcher.LocalPortraitSent = true;   // image goes over once
                p.ColourSlot = PlayerColours.SlotOf(MPConfig.PlayerId);   // 2026-09-05 colours
                Broadcast(MessageEnvelope.Create(MessageType.PlayerProfile, "host", p));
                Plugin.Logger.LogInfo($"[Server] Broadcast host profile: PlayerId='{MPConfig.PlayerId}' CharacterName='{name}' age={age} portrait={(string.IsNullOrEmpty(portrait) ? "none" : "yes")}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastHostProfile: {ex.Message}"); }
        }

        /// <summary>Write the host's own character name into the name maps right
        /// away (no broadcast) so any rivals/business snapshot built BEFORE the
        /// full profile broadcast already resolves the host's in-character name
        /// instead of the PlayerId.  Main thread (reads CharacterData); no-op
        /// until the real name is available.</summary>
        public static void SeedHostName()
        {
            try
            {
                string name = MPNames.LocalCharacterName();
                if (string.IsNullOrWhiteSpace(name) || name == MPConfig.PlayerId) return;
                _characterNamesByPlayerId[MPConfig.PlayerId] = name;
                GameStatePatcher.ClientRivalNames[MPConfig.PlayerId] = name;
            }
            catch { }
        }

        // ── Rivals roster sync (Phase 1d Wave 2) ─────────────────────────────

        /// <summary>
        /// Build a snapshot of host's AI rival roster (id + name pairs) by
        /// walking gi.rivalStates + resolving each id via GetRivalName.  Wave 3
        /// also injects an entry for every human player in the session (their
        /// PlayerId as both id and name for now — display name UX is later).
        /// Receivers (clients) get matching name entries so building popups
        /// can resolve "owned by [player]" without an "undefined" fallback.
        /// </summary>
        public static RivalsSnapshotPayload BuildRivalsSnapshot()
        {
            var snap = new RivalsSnapshotPayload();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi != null && gi.rivalStates != null)
                {
                    foreach (var rs in gi.rivalStates)
                    {
                        if (rs == null) continue;
                        string id = rs.rivalId?.ToString() ?? "";
                        if (string.IsNullOrEmpty(id)) continue;
                        string name = "";
                        try { name = BigAmbitions.Rivals.RivalsHelper.GetRivalName(id) ?? ""; } catch { }
                        snap.Rivals.Add(new RivalInfo { Id = id, Name = name });
                    }
                }

                // RIVAL-FAIR-2 R3: the host's own rival STATE rides the identity snapshot, so a
                // joiner is current the moment it lands.  Null-safe on the other side (older host = null).
                snap.States = BuildRivalStates();

                // Inject every human player as a "rival" entry so receivers
                // can resolve "owned by [player X]" lookups.  Includes the host
                // itself so the host's own PlayerId resolves on every client.
                var seen = new System.Collections.Generic.HashSet<string>();
                foreach (var r in snap.Rivals) if (!string.IsNullOrEmpty(r.Id)) seen.Add(r.Id);

                // Round-257: ship the wholesale/import id arrays verbatim — the client
                // seeds its UUID queue from THESE (slot-exact), never from list order.
                try
                {
                    if (gi?.wholesaleRivalIds != null)
                        foreach (var w in gi.wholesaleRivalIds) { var s = w?.ToString() ?? ""; if (!string.IsNullOrEmpty(s)) snap.WholesaleIds.Add(s); }
                    if (gi?.importRivalIds != null)
                        foreach (var im in gi.importRivalIds) { var s = im?.ToString() ?? ""; if (!string.IsNullOrEmpty(s)) snap.ImportIds.Add(s); }
                }
                catch { }

                // Host itself — marked IsPlayer so client renders via Postfix-injected
                // button instead of consuming a slot in the UUID queue (which never held
                // the special-rival ids to begin with — round-257; the old "sized exactly"
                // comment here was never true and caused the wave-6 shift bug).
                if (!string.IsNullOrEmpty(MPConfig.PlayerId) && seen.Add(MPConfig.PlayerId))
                    snap.Rivals.Add(new RivalInfo { Id = MPConfig.PlayerId, Name = DisplayNameFor(MPConfig.PlayerId), IsPlayer = true, ColourSlot = PlayerColours.SlotOf(MPConfig.PlayerId) });   // 2026-09-05 colours
                // All connected/known peers
                foreach (var playerId in LobbyPlayers)
                {
                    if (string.IsNullOrEmpty(playerId)) continue;
                    if (!seen.Add(playerId)) continue;
                    snap.Rivals.Add(new RivalInfo { Id = playerId, Name = DisplayNameFor(playerId), IsPlayer = true, ColourSlot = PlayerColours.SlotOf(playerId) });   // 2026-09-05 colours
                }
                // Review r7 #4: members who DROPPED are still session players — their buildings are held for reconnect and the
                // host's roster never forgets them — but LobbyPlayers loses them, so every client's roster forgot a dropped owner
                // on its next snapshot and the other-player-shop lock / valuation / takeover shields lapsed there. Ship them too.
                // (Client consumers: the roster refill and ClientRivalNames take IsPlayer entries; the leaderboard appends one row per roster key other than the local player's — so a dropped member keeps a row there, as on the host; rival caches / UUID queue skip IsPlayer.)
                foreach (var kv in GameStatePatcher.ClientPlayerRoster)
                {
                    var playerId = kv.Key;
                    if (string.IsNullOrEmpty(playerId) || !seen.Add(playerId)) continue;
                    snap.Rivals.Add(new RivalInfo { Id = playerId, Name = string.IsNullOrEmpty(kv.Value) ? DisplayNameFor(playerId) : kv.Value, IsPlayer = true, ColourSlot = PlayerColours.SlotOf(playerId) });   // 2026-09-05 colours
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BuildRivalsSnapshot: {ex.Message}"); }
            return snap;
        }

        /// <summary>RIVAL-FAIR-2 R3: the local gi.specialRivalStates as the wire carries it.  A pure
        /// reader - it runs on a client too, which is what lets the `rivalsig` lever compare the two
        /// machines.  completedTimelineEntryIds / sentMessageKeys are deliberately not carried: they are
        /// the HOST's timeline bookkeeping and a client runs no timeline.</summary>
        public static List<CbRivalState> BuildRivalStates()
        {
            var list = new List<CbRivalState>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.specialRivalStates == null) return list;
                // Fold e (rig T-BATCH1 run 1): a save can hold the SAME rival id more than once (the fixture's
                // host save carries each special rival twice; a client mirror carried each 21 times). The game
                // reads the FIRST entry per id (RivalsHelper.GetSpecialRivalState :741-750), so the payload carries
                // exactly that one - never the duplicates.
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var st in gi.specialRivalStates)
                {
                    if (st == null || string.IsNullOrEmpty(st.rivalId)) continue;
                    if (!seenIds.Add(st.rivalId)) continue;   // fold e: first per id wins
                    var row = new CbRivalState { RivalId = st.rivalId, IsActive = st.isActive, IsDefeated = st.isDefeated };
                    if (st.defenseStates != null)
                        foreach (var d in st.defenseStates)
                        {
                            if (d == null) continue;
                            var cb = new CbDefense { Mechanic = (int)d.defensiveMechanic, Aggression = (int)d.aggression };
                            try { if (d.timestamp != null) { cb.Day = d.timestamp.Day; cb.Hour = d.timestamp.Hour; cb.Minute = d.timestamp.Minute; } } catch { }
                            if (d.affectedItems != null) foreach (var s in d.affectedItems) if (!string.IsNullOrEmpty(s)) cb.Items.Add(s);
                            if (d.affectedEmployeeIds != null) foreach (var s in d.affectedEmployeeIds) if (!string.IsNullOrEmpty(s)) cb.EmployeeIds.Add(s);
                            row.Defenses.Add(cb);
                        }
                    list.Add(row);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalSync] BuildRivalStates: {ex.Message}"); }
            return list;
        }

        /// <summary>RIVAL-FAIR-2 R3: rivalId | active | defeated | defense count + timestamps.  Two
        /// machines that agree on this agree on the rival state that matters.</summary>
        public static string RivalStateSignature() => SignatureOf(BuildRivalStates());

        /// <summary>The signature deliberately covers rivalId | active | defeated | defense COUNT plus
        /// each defense's timestamp + mechanic - NOT aggression and NOT affectedItems/EmployeeIds.
        /// Native AddDefenseState only ever APPENDS a defense, never mutates one in place, so count +
        /// per-defense timestamp/mechanic already identifies the list exactly; the mutable-looking
        /// fields would only add drift-noise to a divergence check.  The full state (aggression and
        /// items included) still travels: the join snapshot carries BuildRivalStates in full.</summary>
        private static string SignatureOf(List<CbRivalState> states)
        {
            var sb = new System.Text.StringBuilder();
            // Fold e: canonical order (by rival id) so the signature compares across machines whatever
            // order each save happens to hold its states in.
            var ordered = new List<CbRivalState>(states);
            ordered.Sort((a, b) => string.CompareOrdinal(a.RivalId, b.RivalId));
            foreach (var r in ordered)
            {
                sb.Append(r.RivalId).Append('|').Append(r.IsActive ? '1' : '0').Append(r.IsDefeated ? '1' : '0')
                  .Append('|').Append(r.Defenses.Count);
                foreach (var d in r.Defenses)
                    sb.Append(':').Append(d.Day).Append('.').Append(d.Hour).Append('.').Append((int)d.Minute).Append('.').Append(d.Mechanic);
                sb.Append(';');
            }
            return sb.ToString();
        }

        private static string _lastRivalStateSig = "";

        /// <summary>RIVAL-FAIR-2 R3: publish the host's rival state when it CHANGES.  The payload carries
        /// States only - Rivals / WholesaleIds / ImportIds stay empty - because the identity half of the
        /// snapshot reseeds the client's ClientRivalNames, ClientPlayerRoster and the slot-exact
        /// PendingRivalIdQueue, and re-running that on every hourly sweep would churn caches that must be
        /// written once at join.  The client's apply recognises the states-only shape and touches nothing
        /// else.</summary>
        public static void PublishRivalStateIfChanged(string why)
        {
            try
            {
                if (!IsRunning) return;
                var states = BuildRivalStates();
                string sig = SignatureOf(states);
                if (sig == _lastRivalStateSig) return;
                _lastRivalStateSig = sig;
                Broadcast(MessageEnvelope.Create(MessageType.RivalsSnapshot, "host", new RivalsSnapshotPayload { States = states }));
                int k = 0, m = 0;
                foreach (var r in states) { if (r.IsActive) k++; m += r.Defenses.Count; }
                Plugin.Logger.LogInfo($"[RivalSync] host rival state published: {states.Count} rival(s), {k} active, {m} defense(s) ({why}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalSync] publish: {ex.Message}"); }
        }

        public static void SendRivalsSnapshotTo(MPLink peer)
        {
            if (peer == null) return;
            try
            {
                var snap = BuildRivalsSnapshot();
                Send(peer, MessageEnvelope.Create(MessageType.RivalsSnapshot, "host", snap));
                Plugin.Logger.LogInfo($"[Server] Sent rivals snapshot to peer={peer.Id}: {snap.Rivals.Count} rival(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendRivalsSnapshotTo: {ex.Message}"); }
        }

        /// <summary>
        /// The game's exact per-business weekly income, as shown in the rivals
        /// detail breakdown (RivalBusinessesTable.Load, per native decompile):
        ///   RentedByPlayer ? GetAvgDailyIncome(7) * 7
        ///                  : sum of the last 7 entries of reg.dailyIncomes.
        /// AI rival businesses (RentedByPlayer=false) use the simulated
        /// dailyIncomes list (the same source RivalData.WeeklyIncome sums).
        /// </summary>
        public static float WeeklyIncomeForBusiness(BuildingRegistration reg)
        {
            if (reg == null) return 0f;
            try
            {
                if (reg.RentedByPlayer)
                {
                    try { return reg.GetAvgDailyIncome(7) * 7f; } catch { return 0f; }
                }
                var di = reg.dailyIncomes;
                if (di == null) return 0f;
                int c = di.Count;
                int start = c > 7 ? c - 7 : 0;
                float s = 0f;
                for (int i = start; i < c; i++) s += di[i];
                return s;
            }
            catch { return 0f; }
        }

        /// <summary>
        /// Build per-rival stats by iterating gi.BuildingRegistrations and
        /// counting ownership matches per rivalId.  WeeklyIncome currently
        /// approximated as sum of rent revenue from owned-as-landlord buildings;
        /// a finer model (full business revenue) is a later refinement.
        /// </summary>
        public static RivalsStatsSnapshotPayload BuildRivalsStatsSnapshot()
        {
            var snap = new RivalsStatsSnapshotPayload();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return snap;

                // id → stats dict.  AI rivals come from gi.rivalStates; we read
                // their AUTHORITATIVE income/counts straight from each rival's
                // RivalData via the public RivalsHelper.GetRivalData API.  This
                // does NOT depend on the rivals leaderboard UI having been
                // opened — the old AiRivalDataRefs overlay only had data after
                // the host opened the rivals app once, which is exactly the
                // fragility that left AI income at $0 on the client.
                var byId = new System.Collections.Generic.Dictionary<string, RivalStatsInfo>();
                int aiWithData = 0, aiNonZero = 0;
                if (gi.rivalStates != null)
                {
                    foreach (var rs in gi.rivalStates)
                    {
                        if (rs == null) continue;
                        string id = rs.rivalId?.ToString() ?? "";
                        if (string.IsNullOrEmpty(id)) continue;
                        string name = "";
                        try { name = BigAmbitions.Rivals.RivalsHelper.GetRivalName(id) ?? ""; } catch { }
                        var info = new RivalStatsInfo { Id = id, Name = name };
                        try
                        {
                            var rd = BigAmbitions.Rivals.RivalsHelper.GetRivalData(id);
                            if (rd != null)
                            {
                                info.WeeklyIncome         = rd.WeeklyIncome;            // authoritative game calc
                                // Leaderboard ROW counts the Retail/Office subset
                                // (HostCountDiag: lb.ownedBusinesses.Count ==
                                // rd.ownedRetailOfficeBusinesses.Count) — this is why
                                // the host excludes factories from the count.  Send
                                // THAT so the client's row matches.
                                info.OwnedBusinessesCount = rd.ownedRetailOfficeBusinesses?.Count ?? 0;
                                info.OwnedBuildingsCount  = rd.ownedBuildings?.Count  ?? 0;
                                try { info.MostActiveNeighborhood = rd.MostActiveNeighborhood ?? ""; } catch { }
                                try { info.AgeInYears = BigAmbitions.Rivals.RivalsHelper.GetRivalAgeInYears(rd); } catch { }
                                try { info.IsDefeated = BigAmbitions.Rivals.RivalsHelper.IsRivalDefeated(id); } catch { }
                                aiWithData++;
                                if (info.WeeklyIncome != 0f) aiNonZero++;

                                // Per-business breakdown (host-authoritative).  The
                                // client can't compute AI business income locally
                                // (no sales simulation), so we ship name/type/income
                                // per business, keyed by AddressKey.  Iterate
                                // ownedRetailOfficeBusinesses — that is the EXACT set
                                // the leaderboard counts AND the detail breakdown
                                // shows (Retail+Office+Cinema+Theater; factory/
                                // warehouse excluded).  Confirmed via HostBizDiag
                                // (which listed a Cinema + Theater but no factory).
                                if (rd.ownedRetailOfficeBusinesses != null)
                                {
                                    foreach (var reg in rd.ownedRetailOfficeBusinesses)
                                    {
                                        if (reg == null) continue;
                                        try
                                        {
                                            string addr = GameStateReader.AddressKey(reg);
                                            // EXACT per-business income, replicating the game's
                                            // RivalBusinessesTable.Load (from native decompile):
                                            //   RentedByPlayer ? GetAvgDailyIncome(7)*7
                                            //                  : Sum(last 7 of reg.dailyIncomes)
                                            // AI rival businesses (RentedByPlayer=false) use the
                                            // simulated dailyIncomes list — the same source the
                                            // matching leaderboard total sums.  Fully computable
                                            // here for every rival; no UI capture needed.
                                            float wkInc = WeeklyIncomeForBusiness(reg);

                                            info.Businesses.Add(new RivalBusinessInfo
                                            {
                                                AddressKey   = addr,
                                                BusinessName = reg.BusinessName?.ToString() ?? "",
                                                BusinessType = reg.businessTypeName ?? "",
                                                WeeklyIncome = wkInc,
                                            });
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] GetRivalData('{id}'): {ex.Message}"); }
                        byId[id] = info;
                    }
                }
                Plugin.Logger.LogInfo($"[Server] AI stats via GetRivalData: rivals={byId.Count} withData={aiWithData} nonZeroIncome={aiNonZero}.");

                // Seed host + connected players so they appear with at least
                // their identity even before they own anything.  Players are
                // NOT in RivalDataCache, so GetRivalData skipped them above.
                void SeedPlayer(string playerId)
                {
                    if (string.IsNullOrEmpty(playerId)) return;
                    if (!byId.ContainsKey(playerId))
                        byId[playerId] = new RivalStatsInfo { Id = playerId, Name = DisplayNameFor(playerId) };
                }
                SeedPlayer(MPConfig.PlayerId);
                foreach (var p in LobbyPlayers) SeedPlayer(p);

                // Other connected clients: use their self-reported stats — the
                // host's gi doesn't know what other players own (no client→host
                // ownership sync).  Pushed via RivalsStatsRequest when that
                // client opens its own rivals app.
                foreach (var kv in _clientSelfStats)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (kv.Key == MPConfig.PlayerId) continue;   // host computes own below
                    if (!byId.TryGetValue(kv.Key, out var entry)) continue;
                    var req = kv.Value;
                    entry.OwnedBuildingsCount  = req.SelfOwnedBuildingsCount;
                    entry.OwnedBusinessesCount = req.SelfOwnedBusinessesCount;
                    entry.WeeklyIncome         = req.SelfWeeklyIncome;
                    entry.MostActiveNeighborhood = req.SelfNeighborhood ?? "";   // round-25 parity
                    // Per-business rows + real per-day series ride through to
                    // every machine (detail breakdown, dailyIncomes feed, graphs).
                    if (req.Businesses      != null && req.Businesses.Count      > 0) entry.Businesses      = req.Businesses;
                    if (req.IncomeHistory   != null && req.IncomeHistory.Count   > 0) entry.IncomeHistory   = req.IncomeHistory;
                    if (req.BizCountHistory != null && req.BizCountHistory.Count > 0) entry.BizCountHistory = req.BizCountHistory;
                }

                // Host's OWN player stats from gi.  AI rival counts now come
                // from GetRivalData above, so this only handles the host
                // player's owned buildings / operated businesses — no per-reg
                // AI counting (which previously DOUBLE-COUNTED on top of the
                // RivalData numbers and corrupted AI income).
                if (byId.TryGetValue(MPConfig.PlayerId, out var hostStat) && gi.BuildingRegistrations != null)
                {
                    // Round-25 parity: publish EXACTLY what the host's own native self-sheet shows (shared
                    // builder with the client self-report). Replaces the RentPerDay×7 "income" (rent EXPENSE,
                    // not income — the header couldn't even match the sum of its own rows) and adds the
                    // primary neighborhood, which was never published for players.
                    RivalSelfStats.Build(out int hBldgs, out float hIncome, out string hHood, out var hRows);
                    hostStat.OwnedBuildingsCount    = hBldgs;
                    hostStat.OwnedBusinessesCount   = hRows.Count;
                    hostStat.WeeklyIncome           = hIncome;
                    hostStat.MostActiveNeighborhood = hHood;
                    hostStat.Businesses             = hRows;
                    // Host's own real per-day series (same source clients send).
                    try
                    {
                        if (gi.playerWeeklyIncomeHistory != null)
                            foreach (var t in gi.playerWeeklyIncomeHistory)
                                if (t != null) hostStat.IncomeHistory.Add(new HistoryPointF { Day = t.Item1, Value = t.Item2 });
                        if (hostStat.IncomeHistory.Count > 10) hostStat.IncomeHistory.RemoveRange(0, hostStat.IncomeHistory.Count - 10);
                        if (gi.playerNumberOfBusinessesHistory != null)
                            foreach (var t in gi.playerNumberOfBusinessesHistory)
                                if (t != null) hostStat.BizCountHistory.Add(new HistoryPointI { Day = t.Item1, Value = t.Item2 });
                        if (hostStat.BizCountHistory.Count > 10) hostStat.BizCountHistory.RemoveRange(0, hostStat.BizCountHistory.Count - 10);
                    }
                    catch { }
                }

                snap.Stats.AddRange(byId.Values);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BuildRivalsStatsSnapshot: {ex.Message}"); }
            return snap;
        }

        public static void SendRivalsStatsSnapshotTo(MPLink peer)
        {
            if (peer == null) return;
            try
            {
                var snap = BuildRivalsStatsSnapshot();
                Send(peer, MessageEnvelope.Create(MessageType.RivalsStatsSnapshot, "host", snap));
                Plugin.Logger.LogInfo($"[Server] Sent rivals stats snapshot to peer={peer.Id}: {snap.Stats.Count} stat block(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendRivalsStatsSnapshotTo: {ex.Message}"); }
        }

        /// <summary>Round-25 freshness: build the merged rivals stats snapshot ONCE and broadcast it to every
        /// peer — a self-report changes what all viewers should see, and the old request-reply-only flow left
        /// every other machine stale until IT happened to open its own rivals app. MAIN THREAD ONLY.</summary>
        public static void BroadcastRivalsStatsSnapshot()
        {
            if (!_running) return;
            try
            {
                var snap = BuildRivalsStatsSnapshot();
                Broadcast(MessageEnvelope.Create(MessageType.RivalsStatsSnapshot, "host", snap));
                Plugin.Logger.LogInfo($"[Server] Broadcast rivals stats snapshot: {snap.Stats.Count} stat block(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastRivalsStatsSnapshot: {ex.Message}"); }
        }

        // Collapse bursts: at go-live the release path AND the first for-sale check
        // both want to send a full table within a few hundred ms.  One is enough.
        private static int _lastFullSnapshotTick = -100000;

        /// <summary>Broadcast the full business table to all peers (on for-sale list change).</summary>
        /// <summary>v9 (T6): the buy-marketplace list alone — the daily for-sale refresh's
        /// replacement for a full business-snapshot broadcast.</summary>
        public static void BroadcastBuildingsForSale(List<BuildingForSaleInfo> list)
        {
            if (!_running || list == null) return;
            try
            {
                Broadcast(MessageEnvelope.Create(MessageType.BuildingsForSale, "host",
                          new BuildingsForSalePayload { BuildingsForSale = list }));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastBuildingsForSale: {ex.Message}"); }
        }

        public static void BroadcastBusinessSnapshot()
        {
            if (!_running) return;
            if (Environment.TickCount - _lastFullSnapshotTick < 3000)
            {
                Plugin.Logger.LogInfo("[Server] BusinessSnapshot broadcast skipped (duplicate within 3s).");
                MPLoadProfiler.Mark("HOST BusinessSnapshot broadcast SKIPPED (dedupe <3s)");
                return;
            }
            _lastFullSnapshotTick = Environment.TickCount;
            try
            {
                long t0 = MPLoadProfiler.NowMs;
                var snap = BusinessSync.BuildFullSnapshot();
                MPLoadProfiler.Span($"HOST BuildFullSnapshot (broadcast; {snap.Businesses.Count} buildings)", t0);
                var env = MessageEnvelope.Create(MessageType.BusinessSnapshot, "host", snap);
                int bytes = env.Serialize().Length;
                Broadcast(env);
                // v9: every connected peer just received this full table — rebase their
                // per-business baselines so the WorldReady delta compares against reality.
                var sigs = PerBusinessSigs(snap);
                foreach (var pr in _clients.Keys) _joinBizSigs[pr.Id] = sigs;
                MPLoadProfiler.Mark($"HOST broadcast BusinessSnapshot: {bytes} bytes ({snap.Businesses.Count} buildings)");
                Plugin.Logger.LogInfo($"[Server] Broadcast business snapshot: {snap.Businesses.Count} buildings, {snap.BuildingsForSale.Count} for-sale.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastBusinessSnapshot: {ex.Message}"); }
        }

        public static void BroadcastParkedSnapshot(ParkedSnapshotPayload payload)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(MessageType.ParkedSnapshot, "host", payload));
        }

        /// <summary>Host: send ONE peer a parked-vehicle snapshot (per-peer resync
        /// on teleport / building-exit / (re)join).</summary>
        public static void SendParkedSnapshotTo(MPLink peer, ParkedSnapshotPayload payload)
        {
            if (!_running || peer == null || payload == null) return;
            Send(peer, MessageEnvelope.Create(MessageType.ParkedSnapshot, "host", payload));
        }

        /// <summary>Host: connected client peers paired with their player ids
        /// (snapshot copy; both backing maps are concurrent, safe to enumerate).</summary>
        public static List<(MPLink peer, string playerId)> ConnectedClientPeers()
        {
            var list = new List<(MPLink, string)>();
            foreach (var peer in _clients.Keys)
                if (_peerNames.TryGetValue(peer.Id, out var pid) && !string.IsNullOrEmpty(pid))
                    list.Add((peer, pid));
            return list;
        }

        // ── Broadcast helpers ─────────────────────────────────────────────────

        public static void BroadcastRentConfirmToClients(string addressKey, float dailyRent, float lastDeposit)
        {
            var payload = new BuildingOwnershipPayload
            {
                AddressKey    = addressKey,
                OwnerPlayerId = "host",
                DailyRent     = dailyRent,
                LastDeposit   = lastDeposit
            };
            Broadcast(MessageEnvelope.Create(MessageType.RentConfirm, "host", payload));
        }

        /// <summary>Round-238 (orphan-claim adoption): RentConfirm naming <paramref name="ownerPid"/>
        /// to every client EXCEPT the owner — the owner's copy already holds the tenancy (its native
        /// claim is the heal's evidence), the same exclusion a HandleRentRequest grant uses.</summary>
        public static void BroadcastRentConfirmExcept(string ownerPid, string addressKey, float dailyRent, float lastDeposit)
        {
            var payload = new BuildingOwnershipPayload
            {
                AddressKey    = addressKey,
                OwnerPlayerId = ownerPid,
                DailyRent     = dailyRent,
                LastDeposit   = lastDeposit
            };
            var env = MessageEnvelope.Create(MessageType.RentConfirm, "host", payload);
            int sentTo = 0;
            foreach (var peer in _clients.Keys)
                if (!_peerNames.TryGetValue(peer.Id, out var pid) || pid != ownerPid)
                { Send(peer, env); sentTo++; }
            Plugin.Logger.LogInfo($"[Server] RentConfirm for '{addressKey}' → '{ownerPid}' relayed to {sentTo} other client(s) (round-238 adoption).");
        }

        private static void BroadcastLobbyUpdate()
        {
            var payload = new LobbyUpdatePayload
            {
                Players = new List<string>(LobbyPlayers),
                EnforceStartingCash = EnforceStartingCash,
                Ages = new Dictionary<string, int>(StartingAgeByPlayer),
                LoadMode = !string.IsNullOrEmpty(ChosenLoadSession),   // resuming a save
                LoadSessionName = ChosenLoadSession,
                // Round-283: "this host drops stale phase reports by Seq" — a literal true, because
                // the flag's meaning is a compile-time fact about THIS assembly (the guard is in
                // RecordPhaseReport), not a runtime setting.  Same shape as HelloPayload.CargoDelta.
                // 284b (verifier F-6): the two directions gate differently ON PURPOSE.  Host→client
                // express requires flag + version equality; client→host requires this flag + a
                // non-zero ServedLoadGen — the client can't check version equality (this payload
                // carries no host version), and for the one hazard that matters (the baseline fire
                // rule) a minted ticket is STRONGER proof than a version string: only a host running
                // the gen-keyed rule ever stamps one.
                HostExpress = true,
            };
            Broadcast(MessageEnvelope.Create(MessageType.LobbyUpdate, "host", payload));
        }

        public static void BroadcastVacate(string addressKey)
        {
            var payload = new BuildingOwnershipPayload { AddressKey = addressKey, OwnerPlayerId = "" };
            Broadcast(MessageEnvelope.Create(MessageType.VacateNotify, "host", payload));
        }

        /// <summary>
        /// Broadcasts the host's own player position to all connected clients.
        /// Called from MPCanvasUI at ~10 Hz.
        /// </summary>
        public static void BroadcastPlayerPosition(PlayerPositionPayload payload)
        {
            if (!_running) return;
            var env    = MessageEnvelope.Create(MessageType.PlayerMove, MPConfig.PlayerId, payload);
            var bytes = env.Serialize();
            foreach (var peer in _clients.Keys)
                peer.Send(bytes, reliable: false);
        }

        /// <summary>
        /// Sends a full WorldSnapshot to every connected client.
        /// Called ~4 seconds after the host detects the game scene has loaded.
        /// </summary>
        // Toggle for host's WorldSnapshot broadcast.  Gating was added during
        // the backlog #6 entry-bug investigation; default restored to true so
        // rent sync continues to work in normal play.  Can be flipped at
        // runtime for ad-hoc diagnostic experiments.
        public static bool BroadcastWorldSnapshotEnabled { get; set; } = true;

        public static void BroadcastWorldSnapshotToAll()
        {
            if (!_running) return;
            if (!BroadcastWorldSnapshotEnabled)
            {
                Plugin.Logger.LogWarning("[Server] WorldSnapshot broadcast SKIPPED (host kill-switch active).");
                return;
            }
            var snapshot = BuildWorldSnapshot();
            Broadcast(MessageEnvelope.Create(MessageType.Welcome, "host", snapshot));
            Plugin.Logger.LogInfo("[Server] WorldSnapshot broadcast to all clients.");
        }

        private static void BroadcastPlayerLeft(string playerId)
        {
            var payload = new PlayerLeftPayload { PlayerId = playerId };
            Broadcast(MessageEnvelope.Create(MessageType.PlayerLeft, "host", payload));
            Plugin.Logger.LogInfo($"[Server] PlayerLeft broadcast for '{playerId}'");
        }

        public static void BroadcastMarketSnapshot(string marketJson)
        {
            var payload = new MarketSnapshotPayload { MarketEntriesJson = marketJson };
            Broadcast(MessageEnvelope.Create(MessageType.MarketSnapshot, "host", payload));
        }

        /// <summary>
        /// Broadcasts the host's current game day and time-of-day to all clients.
        /// Called from MPCanvasUI every few seconds as a drift-alignment heartbeat.
        /// </summary>
        private static int   _lastLoggedGtsDay   = int.MinValue;   // round-189: log on day/speed transitions only
        private static float _lastLoggedGtsSpeed = float.MinValue;
        /// <summary>Round-283: the clock heartbeat's freshness stamp — monotonic for the life of this
        /// host process, never reset, so it can only ever move forward for a given receiver.</summary>
        private static long _gtsSeq;
        private static int  _gtsExpressPeers = -1;   // last logged express/total split (log on change only)

        public static void BroadcastGameTime(float? speedOverride = null)
        {
            if (!_running) return;
            // RIVAL-FAIR-2 fold g (rig T-BATCH1 run 2): a rival's activation/deactivation can land in a
            // MONOLOGUE CALLBACK after the hooked timeline sweep returned (the client showed a rival active
            // that the host had already deactivated), so the change-gated publish also rides this 3 s
            // heartbeat - a poll of the authoritative state, sent only when the signature moved.
            try { PublishRivalStateIfChanged("heartbeat"); } catch (Exception rx) { Plugin.Logger.LogWarning($"[RivalSync] heartbeat publish: {rx.Message}"); }
            var (day, hour) = GameStateReader.GetGameTime();
            float speed = speedOverride ?? UnityEngine.Time.timeScale;

            var payload = new GameTimeSyncPayload
            {
                Day = day, TimeOfDay = hour, Speed = speed, RainState = MPWeatherSync.CurrentRainState(),
                RainIntensity = MPWeatherSync.CurrentRainIntensity(),
                // SPEED-SHARED: the host's slider is the session clock. Send the PREFERENCE (OptionsGuard.HostValue), not the live
                // MinutesMultiplier field: the pin prefix keeps the two equal in a session, but the preference is the source the
                // host's own slider writes first (Options.cs:1219) and it cannot be caught mid-call by another setter.
                ClockMult = OptionsGuard.HostValue(),
                TuneDrain = MPNeedsTuning.DrainPercent, TuneRest = MPNeedsTuning.RestPercent,
                TuneMorale = MPNeedsTuning.MoralePercent,
                TunePowerNap = MPNeedsTuning.PowerNapAllowed ? 1 : 0,   // POWERNAP host gate rides the 3s heartbeat
                Seq = System.Threading.Interlocked.Increment(ref _gtsSeq),   // round-283 freshness stamp
                // Round-284/F2: pause INTENT rides the heartbeat — a LIVE read at send time of
                // the same synchronously-flipped fields the F1 join inform reads (never the
                // main-thread-lagged TimeSync.ManualPaused).  Clients converge to it, so a
                // lost/misordered ManualPause edge heals within one heartbeat.
                PauseState = (_deliberatePause || _pausedByDisconnect) ? 1 : 2,
            };

            // ── Round-283 ORDER-SAFETY AUDIT: the clock heartbeat on the express lane ──
            // Why it is safe for this packet to overtake ANY bulk message in flight to this
            // peer (store mirrors, world/business/interior snapshots, LoadData — everything):
            //
            // It carries ABSOLUTE STATE, and every one of its consumers recomputes from
            // scratch on arrival rather than accumulating.  TimeSync.ReceiveClockSync reads
            // the client's LIVE local clock (GameStateReader.GetGameTime) at apply time and
            // derives the whole correction from the difference — it is not a delta chained
            // onto a previous packet, so a packet that arrives early, late, or alone still
            // produces the correct answer for the moment it is applied.  The same holds for
            // its passengers: MPNeedsTuning.SetFromHeartbeat assigns absolute percents;
            // MPWeatherSync.ApplyRainState re-reads the LIVE local rain state and acts only
            // on a mismatch (a live read at commitment, so it converges from any order);
            // TimeSync.ApplyNetwork writes an absolute timeScale (round-283 verifier
            // precision: not the SOLE timeScale writer — StartupRelease and the per-frame
            // ManualPause pin also write it, so Speed survives at most one frame against
            // the pin; all are live-state writers, none order-dependent).  No consumer
            // composes the clock packet with another message type by ARRIVAL ORDER — the
            // cross-type touches (ReceiveClockSync's `if (MPRestSync.SkipActive) return`,
            // the tick patch's re-read of SkipActive, MPRestSync clearing AheadHeld) are
            // all LIVE reads/writes of current state, and each interleaving is already
            // reachable today under the ordered lane.
            //
            // What arriving out of order WOULD break, and what stops it: two clock packets
            // reordered against EACH OTHER.  The older one's drift is computed against a
            // clock the newer one already corrected, so the ahead-branch would latch
            // AheadHeld and freeze a perfectly aligned client's game-time tick until the next
            // heartbeat.  That is what Seq is for — the client drops any packet at or below
            // the newest it has applied.  Order WITHIN the express lane is preserved anyway;
            // Seq covers the seam where a peer's first express packet passes an ordinary one
            // still sitting in the bulk queue.
            var env = MessageEnvelope.Create(MessageType.GameTimeSync, "host", payload);
            var bytes = env.Serialize();
            int express = 0, total = 0;
            foreach (var peer in _clients.Keys)
            {
                total++;
                // Per-peer isolation: one throwing link must not cost the others their heartbeat.
                try
                {
                    // Capability-gated: a peer that did not announce the Seq guard keeps the
                    // ordered lane, which is byte-identical to what it gets today.
                    if (IsExpressCapablePeer(peer.Id)) { peer.SendExpress(bytes); express++; }
                    else                                peer.Send(bytes, reliable: true);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] GameTimeSync → {peer.Describe}: {ex.Message}"); }
            }
            if (total > 0 && express != _gtsExpressPeers)
            {
                _gtsExpressPeers = express;
                Plugin.Logger.LogInfo($"[Server] GameTimeSync lane: {express}/{total} peer(s) express-capable "
                                    + $"(the rest keep the ordered lane — round-283).");
            }
            // Round-189 (user-approved): the heartbeat logged every send (1,541 lines in one field
            // log).  It is the triage TIME ANCHOR — keep it, but only on day/speed transitions
            // (the client side has been day-gated the same way for a while).
            if (day != _lastLoggedGtsDay || System.Math.Abs(speed - _lastLoggedGtsSpeed) > 0.01f)
            {
                _lastLoggedGtsDay = day; _lastLoggedGtsSpeed = speed;
                Plugin.Logger.LogInfo($"[Server] GameTimeSync: day={day} hour={hour:F1} speed={speed:F2}×");
            }
        }

        private static void Broadcast(MessageEnvelope env)
        {
            if (_transport == null) return;
            var bytes = env.Serialize();
            foreach (var peer in _clients.Keys)
                peer.Send(bytes, reliable: true);
        }

        /// <summary>Public wrapper around Broadcast so external code (e.g. Harmony patches) can use it.</summary>
        public static void BroadcastAny(MessageEnvelope env) => Broadcast(env);

        /// <summary>Host: broadcast a Business Hub payload to every client.</summary>
        public static void BroadcastHub<T>(MessageType type, T payload) where T : class
        {
            if (!_running || payload == null) return;
            Broadcast(MessageEnvelope.Create(type, "host", payload));
        }

        /// <summary>Host: send a Business Hub payload to ONE player.</summary>
        public static void SendHubTo<T>(string playerId, MessageType type, T payload) where T : class
        {
            if (!_running || payload == null || string.IsNullOrEmpty(playerId)) return;
            try
            {
                foreach (var kv in _peerNames)
                {
                    if (kv.Value != playerId) continue;
                    foreach (var peer in _clients.Keys)
                        if (peer.Id == kv.Key)
                        {
                            Send(peer, MessageEnvelope.Create(type, "host", payload));
                            return;
                        }
                }
                Plugin.Logger.LogWarning($"[Server] hub message: '{playerId}' not connected.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendHubTo: {ex.Message}"); }
        }

        /// <summary>Host (handoff slice 1): send one session-store mirror piece to every
        /// client EXCEPT the member whose .hsg it is (their own local save writes that
        /// file — sending it back could only race it). exceptStable "" = everyone
        /// (manifest-only pieces).</summary>
        /// <summary>v9: per-client memory of which mirrored file signature each stableId was
        /// already sent this session — absent members' carried-forward .hsg is byte-identical
        /// sweep after sweep, and re-shipping ~2 MB per save per client bought nothing.
        /// Key = receiving client's stableId → (fileKey → FNV sig). Cleared per-client at
        /// disconnect (their next session re-receives once) and wholesale at server stop.</summary>
        private static readonly Dictionary<string, Dictionary<string, long>> _mirrorSentByStable = new();

        internal static void ForgetMirrorMemory(string stableId)
        {
            if (string.IsNullOrEmpty(stableId)) return;
            lock (_mirrorSentByStable) _mirrorSentByStable.Remove(stableId);
        }

        /// <summary>v9 review MIN-5: the session-scoped v9 maps, cleared together at Stop and
        /// at every StartNewGame/StartLoadGame re-arm (main thread on all callers).</summary>
        private static void ClearV9SessionMaps()
        {
            lock (_mirrorSentByStable) _mirrorSentByStable.Clear();
            _joinBizSigs.Clear();
            _peerApplying.Clear();
            lock (_lookCacheByAddr) _lookCacheByAddr.Clear();   // T8
            _bldgByPeer.Clear();                                // T8: presence map dies with the session
        }

        /// <summary>v9 review BLOCKER-2: a client confirmed it holds a mirrored .hsg (written to
        /// its store, or already present via the shared-store token). This is the ONLY writer of
        /// the skip memory — poll thread, lock-guarded.</summary>
        internal static void RecordMirrorAck(string senderPid, MirrorAckPayload p)
        {
            if (p == null || string.IsNullOrEmpty(senderPid) || p.Sig == 0) return;
            if (!StableIdByPlayer.TryGetValue(senderPid, out var receiverStable) || string.IsNullOrEmpty(receiverStable)) return;
            string fileKey = p.SessionName + "|" + p.StableId;
            lock (_mirrorSentByStable)
            {
                if (!_mirrorSentByStable.TryGetValue(receiverStable, out var sentMap))
                    _mirrorSentByStable[receiverStable] = sentMap = new Dictionary<string, long>();
                sentMap[fileKey] = p.Sig;
            }
        }

        public static void SendStoreMirror(StoreMirrorPayload payload, string exceptStable, string fileKey = "", long fileSig = 0)
        {
            if (!_running || payload == null) return;
            try
            {
                var env = MessageEnvelope.Create(MessageType.StoreMirror, "host", payload);
                env.Attachment = payload.HsgRaw;   // v9: raw gzip rides the attachment frame (JsonIgnore keeps it out of Data)
                // Round-282: serialize ONCE for the whole fan-out (Broadcast already
                // does this) — on the megabyte class, per-peer serialization was
                // re-encoding 1-2MB of base64 for every client.
                var bytes = env.Serialize();
                // Round-282 (mirror pacing): the store mirror is THE megabyte class —
                // field 20260818-215459 measured ~6.6MB queued per join-save cycle
                // against links draining at 250-330KB/s, and the clock/phase messages
                // behind that convoy in the single ordered FIFO were 30s+ late.  These
                // mirrors are background replication for host handoff: they tolerate
                // lateness by design, so they take the METERED lane and let urgent
                // gameplay traffic through between chunks.
                //
                // SCOPE DECISION (v1 = StoreMirror only): join-serve LoadData, member
                // SaveData uploads, and world/business snapshots stay IMMEDIATE.  A
                // joining player sits at a loading screen waiting on LoadData — metering
                // that would trade a background delay nobody feels for a foreground one
                // everybody does.  Widen only with a measurement that says otherwise.
                //
                // Supersede key = (session, character).  A newer mirror of the same
                // character's .hsg fully replaces an older one, so an older copy that
                // has not begun sending is dropped rather than shipped as pure convoy.
                // Safe for the manifest a dropped piece may have carried: the sweep
                // sends the manifest as its own piece (key "<session>|"), and a
                // superseding piece for the same character is by definition the fresher
                // snapshot of that file.  Partially-sent payloads are never dropped.
                string key = payload.SessionName + "|" + payload.StableId;
                int skipped = 0;
                foreach (var peer in _clients.Keys)
                {
                    if (!_peerNames.TryGetValue(peer.Id, out var pid)) continue;
                    string peerStable = StableIdByPlayer.TryGetValue(pid, out var st) ? st : "";
                    if (!string.IsNullOrEmpty(exceptStable) && peerStable == exceptStable) continue;
                    // v9 absent-member skip: this exact file already went to this client.
                    if (fileKey.Length > 0 && fileSig != 0 && peerStable.Length > 0)
                    {
                        lock (_mirrorSentByStable)
                        {
                            if (_mirrorSentByStable.TryGetValue(peerStable, out var sentMap)
                                && sentMap.TryGetValue(fileKey, out var had) && had == fileSig)
                            { skipped++; continue; }
                        }
                    }
                    // Per-peer isolation (review fix 2026-07-23): one throwing peer must
                    // not skip the remaining peers' mirror piece.
                    // v9 review BLOCKER-2: delivery is recorded ONLY when the client's
                    // MirrorAck arrives (RecordMirrorAck) — never here. SendPaced merely
                    // QUEUES onto a lane whose documented loss recovery is "the next save
                    // re-mirrors"; a send-time record silently deleted that recovery, with
                    // character loss on host handoff as the end state.
                    try { peer.SendPaced(bytes, key); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendStoreMirror → '{pid}': {ex.Message}"); }
                }
                if (skipped > 0) Plugin.Logger.LogInfo($"[Server] Store mirror '{key}': skipped {skipped} client(s) that already hold this exact file (v9).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendStoreMirror: {ex.Message}"); }
        }

        // ── Round-282b: quit-drain thresholds ────────────────────────────────
        // The round-282 fixed 8s budget was wrong in the direction that matters: a
        // ~6.6MB store at the measured 250-330KB/s needs ~22s, so a perfectly healthy
        // transfer timed out and reported the farewell mirror as possibly-lost.  Time
        // is not the right question — PROGRESS is.  Wait as long as bytes are actually
        // moving; stop the moment they are not.
        /// <summary>Absolute ceiling on quit latency, whatever is happening.  Even a
        /// link that IS progressing does not get to hold the window open forever.</summary>
        private const int DrainHardCapMs = 20_000;
        /// <summary>No strict decrease for this long = the link is dead (or the peer
        /// stopped acking); waiting longer only delays the player's exit.</summary>
        private const int DrainIdleCutoffMs = 3_000;
        /// <summary>Progress-report window: how often the "finishing sync" line is
        /// emitted, and the span each report compares against.  Long enough that a
        /// slow-but-real link reads as moving, short enough to notice a stall.</summary>
        private const int DrainProgressWindowMs = 2_000;
        /// <summary>Poll cadence — cheap; each sample is a few reads per link.</summary>
        private const int DrainPollMs = 100;

        /// <summary>Round-282 (the host-handoff correction), round-282b (progress-based
        /// budget): block until every peer has taken everything it is owed, or until
        /// progress stops.  The quit checkpoint's farewell mirror is the one mirror that
        /// cannot afford to be late — a voluntary host switch depends on it — and
        /// round-282 put mirrors on a metered lane, so "queued before teardown" no
        /// longer means "gone before teardown".
        /// Measures MPLink.UnflushedSendBytes, the strictest figure each transport can
        /// give: on Steam links that includes Steam's own PendingReliable AND
        /// SentUnackedReliable, so reaching zero means the peer ACKNOWLEDGED the bytes.
        /// On UDP links it is our queue estimate only (LiteNetLib exposes no unacked
        /// figure) — that limit is documented at LnlLink.
        /// Ends in exactly one of three states, and says which: drained, stopped-idle,
        /// or hard-cap timeout.  A timeout is never worded as a success.
        /// MAIN THREAD (quit path); the pumps that do the draining run on their own
        /// threads, so blocking here does not stop the bytes from moving.</summary>
        internal static void DrainOutboundForQuit()
        {
            if (!_running || _clients.Count == 0) return;   // solo host: nothing is owed to anyone
            try
            {
                long startTicks   = DateTime.UtcNow.Ticks;
                long hardDeadline = startTicks + TimeSpan.TicksPerMillisecond * DrainHardCapMs;
                long lastProgress = startTicks;              // last time the total strictly decreased
                long best         = long.MaxValue;           // lowest total seen so far
                long windowStart  = startTicks;              // current progress-report window
                long windowTotal  = -1;                      // total at that window's start
                while (true)
                {
                    long total = 0; int peers = 0;
                    foreach (var peer in _clients.Keys)
                    {
                        long b = 0; try { b = peer.UnflushedSendBytes; } catch { }
                        if (b > 0) { total += b; peers++; }
                    }
                    long now = DateTime.UtcNow.Ticks;
                    double secs = (now - startTicks) / (double)TimeSpan.TicksPerSecond;
                    if (windowTotal < 0) windowTotal = total;

                    if (total <= 0)
                    {
                        // Round-282c (verifier): "taken" overstated on UDP — LiteNetLib's backlog
                        // figure cannot see the in-flight window, so drain-to-zero there means
                        // "handed to the send window", not "acknowledged".  Say which one is true.
                        int udpLinks = 0, allLinks = 0;
                        try { foreach (var lk in _clients.Keys) { allLinks++; if (lk.Describe != null && lk.Describe.StartsWith("udp:")) udpLinks++; } } catch { }
                        string meaning = udpLinks == 0 ? "every peer ACKNOWLEDGED the farewell mirror"
                                       : udpLinks == allLinks ? "the farewell mirror was handed to the send window (UDP cannot confirm receipt)"
                                       : "Steam peers ACKNOWLEDGED the farewell mirror; UDP peers only had it handed to the send window";
                        Plugin.Logger.LogInfo($"[Server] quit drain: DRAINED after {secs:F1}s — {meaning}.");
                        return;
                    }
                    // Strict decrease = the transfer is alive; re-arm the idle clock.
                    if (total < best) { best = total; lastProgress = now; }

                    if (now - lastProgress >= TimeSpan.TicksPerMillisecond * DrainIdleCutoffMs)
                    {
                        Plugin.Logger.LogWarning($"[Server] quit drain: STOPPED — no progress for {DrainIdleCutoffMs / 1000}s with {total / 1024}KB still owed to {peers} peer(s) after {secs:F1}s; "
                                               + "treating the link as dead. Those peers keep their PREVIOUS mirror for a handoff.");
                        return;
                    }
                    if (now >= hardDeadline)
                    {
                        Plugin.Logger.LogWarning($"[Server] quit drain: TIMEOUT at the {DrainHardCapMs / 1000}s cap — {total / 1024}KB still owed to {peers} peer(s) (still moving, just not fast enough); "
                                               + "part of the farewell mirror did NOT leave. Those peers keep their PREVIOUS mirror for a handoff.");
                        return;
                    }
                    if (now - windowStart >= TimeSpan.TicksPerMillisecond * DrainProgressWindowMs)
                    {
                        long moved = windowTotal - total;    // negative = the queue GREW this window
                        Plugin.Logger.LogInfo($"[Server] finishing sync: {total / 1024}KB left to {peers} peer(s)"
                            + (moved > 0 ? $" ({moved / 1024}KB moved in the last {DrainProgressWindowMs / 1000}s)..." : " (no movement this window)..."));
                        windowStart = now; windowTotal = total;
                    }
                    Thread.Sleep(DrainPollMs);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] quit drain: {ex.Message}"); }
        }

        /// <summary>Round-282b: close every peer link the FLUSHING way, before anything
        /// else tears the transport down.  Steam links take the round-91 linger path
        /// (Close(linger:true, 1000, tag)), which asks Steam to flush queued reliable
        /// data and delivers a reason tag the client can name; SteamHostTransport.Stop's
        /// bare _socket.Close() would discard it.
        /// UDP links are deliberately LEFT ALONE: LiteNetLib's disconnect discards
        /// pending reliable data (decompiled evidence at LnlLink), so a "polite" close
        /// there would destroy the very mirror the drain just waited for.
        /// MAIN THREAD, quit path only — this makes the links unusable by design.</summary>
        internal static void CloseLinksForQuit()
        {
            if (!_running || _clients.Count == 0) return;
            byte[] tag; try { tag = System.Text.Encoding.UTF8.GetBytes("BAMP:hostquit"); } catch { return; }
            int flushed = 0, left = 0;
            foreach (var peer in _clients.Keys)
            {
                try { if (peer.CloseFlushing(tag)) flushed++; else left++; }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] quit close {peer.Describe}: {ex.Message}"); left++; }
            }
            Plugin.Logger.LogInfo($"[Server] quit close: {flushed} link(s) closed with flush+reason tag"
                + (left > 0 ? $", {left} left to the transport's own teardown (no flushing close available)" : "") + ".");
        }

        /// <summary>Host: broadcast the consensus rest/skip state.</summary>
        public static void BroadcastRestState(RestSkipStatePayload p)
        {
            if (!_running || p == null) return;
            Broadcast(MessageEnvelope.Create(MessageType.RestSkipState, "host", p));
        }

        /// <summary>Host: broadcast the host's own changed retail prices.</summary>
        public static void BroadcastRetailPrices(RetailPricesPayload p)
        {
            if (!_running || p == null) return;
            MPPriceSync.HostRecord(p);   // cache for join replay (Class 4): a hot-joiner needs player-shop prices
            Broadcast(MessageEnvelope.Create(MessageType.RetailPrices, "host", p));
        }

        /// <summary>Join replay (anti-pattern Class 4): send every known player-run shop's current price table to
        /// ONE connecting peer. Player-shop prices are broadcast only on CHANGE, so without this a hot-joiner's
        /// price-competition sim runs on stale inputs until an owner re-prices or the joiner enters the shop.
        /// Replays the host's cache of own + relayed price payloads — each carries the correct OwnerId, so the
        /// joiner's own shops are skipped by MPPriceSync.Apply's own-echo guard. Main thread (on-connect path).</summary>
        public static void SendPlayerShopPricesTo(MPLink peer)
        {
            if (!_running || peer == null) return;
            try
            {
                int sent = 0;
                foreach (var p in MPPriceSync.HostCachedPayloads)   // .Values is a thread-safe snapshot
                {
                    if (p == null || string.IsNullOrEmpty(p.AddressKey)) continue;
                    Send(peer, MessageEnvelope.Create(MessageType.RetailPrices, "host", p));
                    sent++;
                }
                if (sent > 0) Plugin.Logger.LogInfo($"[Server] Sent {sent} player-shop price table(s) to peer {peer.Id} (join replay).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendPlayerShopPricesTo: {ex.Message}"); }
        }

        /// <summary>Host: relay a chat line to every connected client.</summary>
        public static void BroadcastChat(string playerId, string text)
        {
            if (!_running || string.IsNullOrWhiteSpace(text)) return;
            Broadcast(MessageEnvelope.Create(MessageType.Chat, "host",
                new ChatPayload { PlayerId = playerId, Text = text }));
        }

        /// <summary>Host: deliver a PRIVATE chat line to one player only.</summary>
        public static void SendChatPrivate(string fromId, string toId, string text)
        {
            if (!_running || string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(toId)) return;
            try
            {
                foreach (var kv in _peerNames)
                {
                    if (kv.Value != toId) continue;
                    foreach (var peer in _clients.Keys)
                        if (peer.Id == kv.Key)
                        {
                            Send(peer, MessageEnvelope.Create(MessageType.Chat, "host",
                                new ChatPayload { PlayerId = fromId, To = toId, Text = text }));
                            return;
                        }
                }
                Plugin.Logger.LogWarning($"[Server] private chat: '{toId}' not connected.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendChatPrivate: {ex.Message}"); }
        }

        private static void Send(MPLink peer, MessageEnvelope env) => peer.Send(env);

        // ── Utilities ─────────────────────────────────────────────────────────

        private static WorldSnapshotPayload BuildWorldSnapshot()
        {
            return new WorldSnapshotPayload
            {
                BuildingOwners   = new Dictionary<string, string>(BuildingOwners),
                BuildingRealEstateOwners = new Dictionary<string, string>(BuildingRealEstateOwners),
                MarketEntriesJson = GameStateReader.GetMarketEntriesJson(),
                SessionId         = MPLog.SessionId,
            };
        }

        // Poll loop lives in LnlHostTransport now (transport seam) — same
        // thread name, cadence, and throw-isolation as before.
    }
}
