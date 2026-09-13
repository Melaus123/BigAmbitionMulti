using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Company MERGER membership — slice 1 of the merger campaign
    /// (.modding/03-systems/company-merger-map.md §12). A merger is a GROUP of players who run their
    /// companies as a single entity ("this is ours" — user contract 2026-07-07). A session can hold
    /// SEVERAL disjoint groups (P1+P2 and P3+P4); a player belongs to at most one.
    ///
    /// Same two-layer shape as GrantSync, for the same reasons:
    ///   • STORE (_groups): groupId → StableId members — the durable truth, HOST-ONLY, persisted in
    ///     the session manifest and join-replayed. Survives members going offline.
    ///   • RUNTIME (_groupByPid): ONLINE members' PlayerId → groupId, host-rebuilt from store + roster
    ///     on every change and replicated to everyone (MergerState). The ONLY thing enforcement reads.
    ///
    /// Slice 1 effect: a merger acts as MUTUAL ALL-KIND GRANTS — GrantSync.IsGranted() unions
    /// MergedRuntime, so every existing permission gate (vehicles, housing, business, storage,
    /// register, and the host's building-access classification) honors the merger through the one
    /// chokepoint, with NO grant-store writes (dissolve = clear membership; manual grants untouched).
    ///
    /// INERTNESS CONTRACT (user, 2026-07-07): no merger record → every check here is false → all
    /// downstream code behaves exactly as before. Single-player doubly inert (no session state).
    /// </summary>
    public static class MergerSync
    {
        // ── Store (StableId space, HOST-ONLY, manifest-persisted) ─────────────
        private static readonly Dictionary<string, HashSet<string>> _groups = new();   // groupId → member stables
        // Phase 1-A (D3): the company's identity beside its membership. FOUNDER = who minted the group;
        // JOIN ORDER = the order members joined it (founder first) — the display name reads off this.
        // MINT SEQUENCE = how old the group is; a union keeps the OLDER group's id (D4-3). All three are
        // manifest-persisted additively (an old manifest with neither still loads: founder = first stored).
        private static readonly Dictionary<string, string>       _founderByGroup = new();   // groupId → founder stable
        private static readonly Dictionary<string, List<string>> _joinOrder      = new();   // groupId → stables in join order
        private static readonly Dictionary<string, long>         _groupSeq       = new();   // groupId → mint sequence (smaller = older)
        private static long _nextGroupSeq = 1;

        public static IReadOnlyDictionary<string, HashSet<string>> StoreGroups => _groups;

        /// <summary>HOST: the founding member's StableId, or "".</summary>
        public static string FounderOfGroup(string groupId)
            => !string.IsNullOrEmpty(groupId) && _founderByGroup.TryGetValue(groupId, out var f) ? f : "";

        /// <summary>HOST: how old a group is (smaller = minted first). 0 = unknown.</summary>
        public static long SeqOfGroup(string groupId)
            => !string.IsNullOrEmpty(groupId) && _groupSeq.TryGetValue(groupId, out var s) ? s : 0;

        /// <summary>HOST: a group's members in JOIN ORDER (founder first). A member with no order record
        /// (an old manifest) is appended in set order, so the list is always the full membership.</summary>
        public static IReadOnlyList<string> JoinOrderOfGroup(string groupId)
        {
            var ordered = new List<string>();
            if (string.IsNullOrEmpty(groupId) || !_groups.TryGetValue(groupId, out var set)) return ordered;
            if (_joinOrder.TryGetValue(groupId, out var list))
                foreach (var s in list) if (set.Contains(s) && !ordered.Contains(s)) ordered.Add(s);
            foreach (var s in set) if (!ordered.Contains(s)) ordered.Add(s);
            return ordered;
        }

        private static List<string> OrderList(string groupId)
        {
            if (!_joinOrder.TryGetValue(groupId, out var list)) { list = new List<string>(); _joinOrder[groupId] = list; }
            return list;
        }

        /// <summary>The group a StableId belongs to, or "".</summary>
        public static string GroupOfStable(string stable)
        {
            if (string.IsNullOrEmpty(stable)) return "";
            foreach (var kv in _groups) if (kv.Value.Contains(stable)) return kv.Key;
            return "";
        }

        /// <summary>Add a member to a group ("" mints a fresh group). Returns the group id used.</summary>
        public static string StoreAdd(string groupId, string stable)
        {
            if (string.IsNullOrEmpty(stable)) return groupId ?? "";
            if (string.IsNullOrEmpty(groupId)) groupId = System.Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!_groups.TryGetValue(groupId, out var set)) { set = new HashSet<string>(); _groups[groupId] = set; }
            if (!_groupSeq.ContainsKey(groupId)) _groupSeq[groupId] = _nextGroupSeq++;   // phase 1-A: mint age
            if (set.Add(stable)) OrderList(groupId).Add(stable);                         // phase 1-A: join order
            if (!_founderByGroup.ContainsKey(groupId)) _founderByGroup[groupId] = stable;
            return groupId;
        }

        /// <summary>HOST, manifest restore only: add a member preserving the STORED join order (call in
        /// that order) and the group's stored mint sequence (0 = an old manifest → a fresh sequence, and
        /// the founder is simply the first member restored, which is the stored order).</summary>
        public static string StoreRestore(string groupId, string stable, long groupSeq)
        {
            string g = StoreAdd(groupId, stable);
            if (groupSeq > 0)
            {
                _groupSeq[g] = groupSeq;
                if (groupSeq >= _nextGroupSeq) _nextGroupSeq = groupSeq + 1;
            }
            return g;
        }

        /// <summary>HOST, phase 1-A (D4-3): absorb group '<paramref name="other"/>' into
        /// '<paramref name="keep"/>' — every member moves, the absorbed group's join order is appended
        /// after the keeper's, and 'other' leaves the store. Callers keep the OLDER group (SeqOfGroup).
        /// NO group outside the two named here is touched (D4-4).</summary>
        public static bool StoreUnion(string keep, string other)
        {
            if (string.IsNullOrEmpty(keep) || string.IsNullOrEmpty(other) || keep == other) return false;
            if (!_groups.TryGetValue(keep, out var kset) || !_groups.ContainsKey(other)) return false;
            foreach (var s in JoinOrderOfGroup(other)) if (kset.Add(s)) OrderList(keep).Add(s);
            _groups.Remove(other); _joinOrder.Remove(other); _founderByGroup.Remove(other); _groupSeq.Remove(other);
            return true;
        }

        /// <summary>Remove a member from whatever group holds them. A group needs 2+ members — a last
        /// pair dissolves that group entirely (other groups are untouched).</summary>
        public static void StoreRemove(string stable)
        {
            string g = GroupOfStable(stable);
            if (string.IsNullOrEmpty(g)) return;
            _groups[g].Remove(stable);
            if (_joinOrder.TryGetValue(g, out var ord)) ord.Remove(stable);
            if (_groups[g].Count < 2)
            {   // dissolve: the company's identity dies with it (other groups untouched)
                _groups.Remove(g); _joinOrder.Remove(g); _founderByGroup.Remove(g); _groupSeq.Remove(g);
            }
            else if (_founderByGroup.TryGetValue(g, out var f) && f == stable)
            {   // the founder left — the next member in join order founds the surviving company
                var rest = JoinOrderOfGroup(g);
                _founderByGroup[g] = rest.Count > 0 ? rest[0] : "";
            }
        }

        /// <summary>HOST, session boundary only (new world / clear-before-apply at manifest restore) —
        /// mirrors GrantSync.ResetStore's lifecycle exactly.</summary>
        public static void ResetStore()
        {
            _groups.Clear(); _joinOrder.Clear(); _founderByGroup.Clear(); _groupSeq.Clear();
            _nextGroupSeq = 1;
        }

        // ── Runtime (PlayerId space, replicated, online-only) ─────────────────
        // Every member whose PlayerId is KNOWN this session, offline-but-known included (Protocol.cs:732-734),
        // not only the online ones - and it now feeds the grant signature through CoMembersOf (~:273).
        private static Dictionary<string, string> _groupByPid = new();      // known pid → groupId
        private static Dictionary<string, MergerGroupInfo> _groupInfo = new();
        private static bool _wasMember;
        /// <summary>Phase 1-B (B1): the game day my last member self-report was pushed for (-1 = none).</summary>
        private static int _lastStatsPushDay = -1;

        /// <summary>DERIVED UI state (r4): the proposer of the first pending offer addressed to me, or
        /// "". Recomputed from the host's offer table on every MergerState message - never written by the
        /// hub, a relay or the test verb, so it cannot outlive the offer behind it.</summary>
        public static string IncomingFromPid = "";
        /// <summary>DERIVED UI state (r4): the player I clicked to propose to while my offer stands ("Cancel
        /// offer" chip), or "". Same single source as IncomingFromPid.</summary>
        public static string OutgoingToPid = "";

        public static bool IAmMember => _groupByPid.ContainsKey(MPConfig.PlayerId);
        /// <summary>My merged company's group id, or "" (slice 4: wallet broadcasts are group-tagged).</summary>
        public static string MyGroupId => _groupByPid.TryGetValue(MPConfig.PlayerId, out var g) ? g : "";
        /// <summary>Is this online player in MY merged company?</summary>
        public static bool IsMemberPid(string pid)
            => !string.IsNullOrEmpty(pid)
               && _groupByPid.TryGetValue(MPConfig.PlayerId, out var mine)
               && _groupByPid.TryGetValue(pid, out var theirs) && mine == theirs;
        /// <summary>Is this online player in ANY merged company (mine or another)?</summary>
        public static bool InAnyGroup(string pid) => !string.IsNullOrEmpty(pid) && _groupByPid.ContainsKey(pid);

        /// <summary>MY company's display roster (offline members included); empty when not merged.</summary>
        public static IReadOnlyList<string> MemberNames
            => _groupByPid.TryGetValue(MPConfig.PlayerId, out var g) && _groupInfo.TryGetValue(g, out var info)
               ? info.MemberNames : (IReadOnlyList<string>)new List<string>();

        /// <summary>Slice 3: the buildings MY company operates that are NOT natively mine — the
        /// ownership-flip target set (host-resolved; my own buildings are excluded receiver-side
        /// by the flip's RentedByPlayer guard, and here by the host filling per-group keys).</summary>
        public static IReadOnlyList<string> MyGroupBuildingKeys
            => _groupByPid.TryGetValue(MPConfig.PlayerId, out var g) && _groupInfo.TryGetValue(g, out var info)
               ? info.BuildingKeys : (IReadOnlyList<string>)new List<string>();

        /// <summary>Phase 1-A (D3): a merged company's display name as the host computed it — the founder's
        /// name then the other members' names in join order. "" when that company is unknown here.</summary>
        public static string GroupDisplayName(string groupId)
            => !string.IsNullOrEmpty(groupId) && _groupInfo.TryGetValue(groupId, out var gi) && gi != null
               ? (gi.DisplayName ?? "") : "";

        /// <summary>Phase 1-A: MY company's display name ("" when I'm not merged).</summary>
        public static string MyGroupDisplayName => GroupDisplayName(MyGroupId);

        /// <summary>Phase 1-A: MY company's founder in PlayerId space ("" when they're offline).</summary>
        public static string MyGroupFounderPid
            => _groupByPid.TryGetValue(MPConfig.PlayerId, out var g) && _groupInfo.TryGetValue(g, out var gi) && gi != null
               ? (gi.FounderPid ?? "") : "";

        /// <summary>Phase 1-A: MY company's full roster in JOIN ORDER (offline members included).</summary>
        public static IReadOnlyList<string> MyMemberNamesOrdered
            => _groupByPid.TryGetValue(MPConfig.PlayerId, out var g) && _groupInfo.TryGetValue(g, out var gi) && gi != null
               ? gi.MemberNamesOrdered : (IReadOnlyList<string>)new List<string>();
        /// <summary>Phase 1-B: MY company's member pids in JOIN ORDER (founder first) - every member whose
        /// PlayerId is KNOWN this session, NOT only the online ones: it returns MemberPidsOrdered, and a
        /// member who connected and left stays listed (Protocol.cs:732-734). Callers that need presence must
        /// test it themselves - the keys-hub company row (MPCanvasUI.cs ~:3008) draws such a member with a
        /// car count of 0. The rivals-list fold reads each member's published stats through these.</summary>
        public static IReadOnlyList<string> MyMemberPidsOrdered
            => _groupByPid.TryGetValue(MPConfig.PlayerId, out var g) && _groupInfo.TryGetValue(g, out var gi) && gi != null
               ? gi.MemberPidsOrdered : (IReadOnlyList<string>)new List<string>();

        /// <summary>Phase 1-B: does this session hold ANY merged company at all? The one cheap test
        /// every fold branch opens with, so an unmerged session pays nothing (inertness contract).</summary>
        public static bool AnyGroup => _groupInfo.Count > 0;

        /// <summary>MY company itself (null when I am not merged) - the rivals fold needs its
        /// BuildingKeys, which cover members who are OFFLINE.</summary>
        public static MergerGroupInfo? MyGroup
            => _groupByPid.TryGetValue(MPConfig.PlayerId, out var g) && _groupInfo.TryGetValue(g, out var gi) ? gi : null;

        /// <summary>r2/R3 - the group MODEL as one string: for every group in id order,
        /// GroupId|DisplayName|MemberNamesOrdered|MemberPidsOrdered|BuildingKeys. Two states with the same
        /// signature are indistinguishable to the rivals list, so a heartbeat that carries no change
        /// must not re-Load an open leaderboard (MergerFlip rebroadcasts every 10 s, and the native
        /// Load destroys the rows and re-selects row 0). Also keys the ForeignGroups cache.</summary>
        private static string GroupModelSignature(Dictionary<string, MergerGroupInfo> src)
        {
            if (src == null || src.Count == 0) return "";
            var ids = new List<string>(src.Keys);
            ids.Sort((a, b) => string.CompareOrdinal(a, b));
            var sb = new System.Text.StringBuilder();
            foreach (var id in ids)
            {
                if (!src.TryGetValue(id, out var g) || g == null) continue;
                sb.Append(id).Append('|').Append(g.DisplayName ?? "").Append('|')
                  .Append(string.Join(",", g.MemberNamesOrdered ?? new List<string>())).Append('|')
                  .Append(string.Join(",", g.MemberPidsOrdered ?? new List<string>())).Append('|')
                  .Append(string.Join(",", g.BuildingKeys ?? new List<string>())).Append(';');   // B2: a company gaining/losing a building changes the folded row
            }
            return sb.ToString();
        }

        /// <summary>Signature of the CURRENT model, and the ForeignGroups cache keyed on it (r2/R5:
        /// the getter allocated + sorted on every call, once per remote player per leaderboard Load).
        /// The sentinel differs from every real signature, so the first read always builds.</summary>
        private static string _sig = "";
        private static string _foreignSig = "\u0001";
        private static IReadOnlyList<MergerGroupInfo> _foreignCache = new List<MergerGroupInfo>();

        /// <summary>Phase 1-B: every merged company I am NOT in, ordered by group id (a stable order,
        /// so two foreign companies always fold into the same two rows). Each stays SEPARATE - groups
        /// are never combined with each other, whatever their size.</summary>
        public static IReadOnlyList<MergerGroupInfo> ForeignGroups
        {
            get
            {
                if (_foreignSig == _sig) return _foreignCache;   // r2/R5: memoised on the R3 signature
                var others = new List<MergerGroupInfo>();
                if (_groupInfo.Count == 0) { _foreignCache = others; _foreignSig = _sig; return others; }
                string mine = MyGroupId;
                foreach (var kv in _groupInfo)
                {
                    if (kv.Value == null || string.IsNullOrEmpty(kv.Key)) continue;
                    if (!string.IsNullOrEmpty(mine) && kv.Key == mine) continue;
                    others.Add(kv.Value);
                }
                others.Sort((a, b) => string.CompareOrdinal(a.GroupId, b.GroupId));
                _foreignCache = others; _foreignSig = _sig;
                return others;
            }
        }

        /// <summary>THE enforcement union read by GrantSync.IsGranted: two distinct online players in
        /// the SAME group hold every key to each other's world.</summary>
        public static bool MergedRuntime(string a, string b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a != b
               && _groupByPid.TryGetValue(a, out var ga) && _groupByPid.TryGetValue(b, out var gb) && ga == gb;

        /// <summary>4d r2: every pid MergedRuntime pairs with <paramref name="pid"/> (same group, distinct) — the
        /// grant signature unions these so a membership edge respawns the ghosts (GrantSync.GrantorSig).</summary>
        public static IEnumerable<string> CoMembersOf(string pid)
        {
            if (string.IsNullOrEmpty(pid) || !_groupByPid.TryGetValue(pid, out var g)) yield break;
            foreach (var kv in _groupByPid) if (kv.Key != pid && kv.Value == g) yield return kv.Key;
        }

        /// <summary>DISSOLVE (2026-09-12): has this machine had a merger state AT ALL in this SESSION? Until
        /// it has, "I am not a member" is UNKNOWN rather than true. FOLD e: a SESSION fact - cleared only
        /// with the session (DropOnDisconnect), never with a scene: a scene reset does not unlearn what the
        /// host said, and the state that arrives during a load must still count once the world is there.</summary>
        public static bool StateSeen { get; private set; }

        /// <summary>FOLD d/e (re-checks of DISSOLVE): EVERY state that says "not a member" ARMS the
        /// whole-world heal here, and MergerDissolve.HealIfPending runs it at whichever comes SECOND of
        /// that state and world-ready, ONCE per world instance (HealedThisWorld). The state can land during
        /// the load, before the world exists (a lobby joiner gets the host's load-time broadcast, a late
        /// joiner the join replay), and for a fully dissolved company nothing ever re-sends it - so the arm
        /// SURVIVES the scene reset that follows and the world-ready hook runs it. Cleared with the session.</summary>
        public static bool HealPending { get; internal set; }

        /// <summary>FOLD e: the whole-world heal ran for THIS world instance. Cleared with the scene, so a
        /// second world load in the same session (the host's next hostload) heals again on ITS first
        /// non-member state; cleared with the session.</summary>
        public static bool HealedThisWorld { get; internal set; }

        /// <summary>ALL machines (host applies its own build; clients apply the broadcast). MAIN THREAD
        /// (may toast). Diff-based membership toasts so formation and dissolution are both announced.</summary>
        public static void ApplyState(MergerStatePayload p)
        {
            string sigBefore = GroupModelSignature(_groupInfo);   // r2/R3: the model as it stands NOW
            var byPid = new Dictionary<string, string>();
            var info  = new Dictionary<string, MergerGroupInfo>();
            if (p?.Groups != null)
                foreach (var g in p.Groups)
                {
                    if (g == null || string.IsNullOrEmpty(g.GroupId)) continue;
                    info[g.GroupId] = g;
                    foreach (var pid in g.MemberPids ?? new List<string>())
                        if (!string.IsNullOrEmpty(pid)) byPid[pid] = g.GroupId;
                }
            // DISSOLVE (2026-09-12, user ruling): who was a co-member of MINE, read BEFORE the model is
            // replaced. At this instant everything that IDENTIFIES an ex-partner is still standing - the
            // flip still holds their buildings' parked runner and their HR shadows still classify a tag -
            // because MergerFlip.Tick only un-flips on its next 1 Hz Update pass and THIS method runs on the
            // MAIN THREAD off the message pump. So the teardown is driven from here, SYNCHRONOUSLY, before
            // anything reconciles. A member who leaves and a member who is left behind both come through
            // this one edge, and a 3+ company that survives one leave names only the leaver.
            var wasCo = new HashSet<string>(CoMembersOf(MPConfig.PlayerId), System.StringComparer.Ordinal);
            _groupByPid = byPid; _groupInfo = info;
            StateSeen = true;
            var exPartners = new List<string>();
            foreach (var was in wasCo)
                if (!string.IsNullOrEmpty(was) && !MergedRuntime(MPConfig.PlayerId, was)) exPartners.Add(was);
            // FOLD c C1 (2026-09-12): TWO edges, never both. The named one above; and the member who was
            // OFFLINE when the company was cancelled, who has nothing to diff - their FIRST state of the
            // session simply says "you are in no group". That is the instant this machine's membership
            // becomes KNOWN. FOLD d/e: EVERY non-member state ARMS the empty-set ("anyone who is not me")
            // sweep, and MergerDissolve.HealIfPending RUNS it at whichever comes second of the state and
            // world-ready, once per world instance - the state usually lands during the load, before the
            // world exists (the host's load-time broadcast, or a join replay), and for a fully dissolved
            // company nothing re-sends it. Never while I am still a member. On a save that was never
            // merged it is value-based and changes nothing.
            if (exPartners.Count > 0) MergerDissolve.Run("membership-edge", exPartners);
            else if (!IAmMember) { HealPending = true; MergerDissolve.HealIfPending("state"); }
            string sigAfter = GroupModelSignature(info);
            _sig = sigAfter;                                      // also invalidates the ForeignGroups cache
            // Phase 1-A r4: BOTH chips are DERIVED from the host's offer table, on every machine, on every
            // state message — nothing else writes them. An offer the host refused silently, retired, or
            // never recorded simply is not here, so no chip can outlive its entry.
            string inc = "", outg = "";
            if (p?.Offers != null)
            {
                // r6 (review r4 #1): the row must name the offer the host's accept/decline will act on - the same
                // precedence as PendingKeyFor: an offer keyed on MY COMPANY first, then one keyed on my pid, and only
                // then any offer that lists me as an addressee.
                string myGroup = byPid.TryGetValue(MPConfig.PlayerId, out var mg) ? mg : "";
                string incGroup = "", incPid = "", incAny = "";
                foreach (var o in p.Offers)
                {
                    if (o == null) continue;
                    if (outg == "" && o.From == MPConfig.PlayerId) outg = o.AskedPid ?? "";
                    if (incGroup == "" && myGroup != "" && o.TargetKey == myGroup) incGroup = o.From ?? "";
                    else if (incPid == "" && o.TargetKey == MPConfig.PlayerId) incPid = o.From ?? "";
                    else if (incAny == "" && o.TargetPids != null && o.TargetPids.Contains(MPConfig.PlayerId)) incAny = o.From ?? "";
                }
                inc = incGroup != "" ? incGroup : incPid != "" ? incPid : incAny;
            }
            IncomingFromPid = inc; OutgoingToPid = outg;
            bool wasMem = _wasMember;
            bool now = IAmMember;
            if (now && !_wasMember)
                PassengerHud.Toast("Company merger active.");
            else if (!now && _wasMember)
                PassengerHud.Toast("Merger dissolved.");
            MergerWallet.OnMembershipEdge(now, _wasMember);   // slice 4: rising edge pools my wallet (host dedupes)
            _wasMember = now;
            // Phase 1-B (B1, r4): a member's rivals figures reach the company row even if they never open
            // the rivals app — the client PUSHES its self-report when it joins the company and once per
            // GAME DAY after that. This method runs on the 10 s state heartbeat, so the day change is
            // noticed within 10 s with no timer of its own. Host members build their own row directly.
            if (!MPServer.IsRunning && MPClient.IsConnected && now)
            {
                int day = 0;
                try { day = GameStateReader.GetGameTime().day; } catch { }
                if (!wasMem || day != _lastStatsPushDay)
                {
                    _lastStatsPushDay = day;
                    MPClient.SendRivalsStatsRequest();
                    Plugin.Logger.LogInfo($"[Merger] rivals self-report pushed (member, day {day})");
                }
            }
            // Phase 1-B (B3): membership decides who the rivals list folds into one company row.
            // Refresh an OPEN leaderboard so forming/joining/leaving shows immediately. Dissolve needs
            // no un-fold - every folded figure is derived per call from the state this just replaced.
            // r2/R3: ONLY when the model actually changed. MergerFlip rebroadcasts MergerState every
            // 10 s; the native Load destroys every row and re-selects row 0, so an unchanged heartbeat
            // must not touch the UI.
            if (sigBefore != sigAfter)
            {
                Plugin.Logger.LogInfo("[Merger] rivals list refresh (group model changed)");
                GameStatePatcher.RefreshRivalLeaderboardIfVisible();
                // 4d r2 (review MAJOR-1): a membership change moves the grant signature (GrantSync.GrantorSig unions
                // co-members) — respawn the ghosts NOW, so an ex-partner's car stops being drivable and loses its
                // kept pin and a new partner's gains both. Idempotent by signature: the host's own call at the end
                // of RefreshGrantsAndBroadcast and the client's PermissionSnapshot apply then see no change.
                VehicleManager.OnGrantsChanged();
            }
        }

        // ══ D25 (user 2026-09-12) — THE BLIP RULE ════════════════════════════════════════
        // A member's merged-company VIEW drops on a disconnect, but only after a SHORT GRACE: a
        // two-second hiccup on the wire must not dissolve the company on this screen and then rebuild
        // it a moment later. OnDisconnected ARMS the grace (non-voluntary drops only); TickDropGrace
        // is a recurring MAIN-THREAD check from MPCanvasUI.Update - an EVENT against the live
        // connection state, never a one-shot delay that assumes the link is still down when it fires.
        // Once the view is dropped IAmMember is false and every subsystem follows its OWN membership
        // edge: MergerFlip.Tick un-flips the partner buildings (its desired set derives from the state
        // cleared here), CompanyBooks.Tick clears the books overlay, the GrantSync union empties, the
        // wallet and the feed/list/candidate/message overlays go inert. The reconnect's MergerState
        // broadcast rebuilds all of it exactly as a first join does.
        private const float GraceSeconds = 3f;
        private static float _dropAt   = -1f;   // unscaledTime the link went down; -1 = no grace armed
        private static float _blipTill = -1f;   // TEST LEVER ONLY (`blip`): treat the link as down until this time

        /// <summary>MAIN THREAD. Arm the grace after a NON-VOLUNTARY disconnect. Idempotent: a second
        /// arm while one stands keeps the first timestamp, so a noisy transport cannot extend it.</summary>
        public static void ArmDropGrace(string why, float blipSeconds = -1f)
        {
            if (!IAmMember) return;                 // nothing to drop - inert outside a merger
            if (_dropAt >= 0f) return;              // already counting
            _dropAt = UnityEngine.Time.unscaledTime;
            _blipTill = blipSeconds >= 0f ? _dropAt + blipSeconds : -1f;
            Plugin.Logger.LogInfo($"[Merger] view held for {GraceSeconds:0} s after a disconnect ({why}).");
        }

        /// <summary>MAIN THREAD, every frame from MPCanvasUI.Update (two float reads when nothing is
        /// armed). Cancels on a reconnect inside the grace, drops the view when the link is still
        /// down after it.</summary>
        public static void TickDropGrace()
        {
            if (_dropAt < 0f) return;
            float now = UnityEngine.Time.unscaledTime;
            // The rig's `blip` lever stands in for the transport loss (MPClient has no re-connectable
            // loss seam: OnDisconnected stops the poll loop and no host address is kept for a rejoin).
            bool linked = _blipTill >= 0f ? now >= _blipTill
                                          : (MPServer.IsRunning || MPClient.IsConnected);
            if (linked)
            {
                _dropAt = -1f; _blipTill = -1f;
                Plugin.Logger.LogInfo("[Merger] view kept - reconnected inside the grace");
                return;
            }
            if (now - _dropAt < GraceSeconds) return;
            float held = now - _dropAt;
            _dropAt = -1f; _blipTill = -1f;
            DropOnDisconnect(held);
        }

        /// <summary>MAIN THREAD. The grace expired with the link still down: the merged-company view
        /// goes. Idempotent - a second call with no state left is a no-op.</summary>
        public static void DropOnDisconnect(float heldSeconds)
        {
            // FOLD f (re-check r3): the three SESSION facts clear even when no group exists - the fully dissolved
            // world is exactly the case they serve, and the early return below would otherwise skip them, letting an
            // arm raised in one session fire in the next.
            StateSeen = false;        // the SESSION boundary - the next state is a FIRST state again
            HealPending = false;      // nothing armed survives the session
            HealedThisWorld = false;
            if (_groupByPid.Count == 0 && _groupInfo.Count == 0) return;
            _groupByPid = new Dictionary<string, string>();
            _groupInfo  = new Dictionary<string, MergerGroupInfo>();
            _sig = ""; _foreignSig = "\u0001";
            _wasMember = false;
            _lastStatsPushDay = -1;
            IncomingFromPid = ""; OutgoingToPid = "";
            Plugin.Logger.LogInfo($"[Merger] view dropped after a {heldSeconds:0} s disconnect");
            // The two subsystems that do NOT hang off the membership edge: the rivals list folds per
            // call off the state just cleared (so an OPEN leaderboard must be told), and the routed
            // cargo transfer keeps per-leg client state that can never complete without a session.
            try { GameStatePatcher.RefreshRivalLeaderboardIfVisible(); } catch { }
            try { VehicleManager.OnGrantsChanged(); } catch { }   // the union just emptied - respawn the ghosts
            try { CargoTransfer.ResetSession(); } catch { }
        }

        /// <summary>SCENE-scoped reset (mirrors GrantSync.ResetSceneState): runtime + local UI state die
        /// with the scene and rebuild from the store (host) or the host broadcast (client). The durable
        /// store's lifecycle is session boundaries, not scene transitions.</summary>
        public static void ResetSceneState()
        {
            _groupByPid = new Dictionary<string, string>();
            _groupInfo  = new Dictionary<string, MergerGroupInfo>();
            _sig = ""; _foreignSig = "\u0001";   // r2/R3+R5: model gone, cache invalid
            _wasMember = false;
            MergerWallet.ResetMirrorFlag();   // WALLET-DUPE-1 (W4): a new world has mirrored no company balance yet
            HealedThisWorld = false;  // FOLD e: a new world instance heals again on its first non-member state;
                                      // StateSeen and HealPending SURVIVE the scene (the state may have landed mid-load)
            _lastStatsPushDay = -1;   // B1: a new scene pushes again on the next state message
            IncomingFromPid = ""; OutgoingToPid = "";
        }
    }
}
