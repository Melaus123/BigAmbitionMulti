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

        public static IReadOnlyDictionary<string, HashSet<string>> StoreGroups => _groups;

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
            set.Add(stable);
            return groupId;
        }

        /// <summary>Remove a member from whatever group holds them. A group needs 2+ members — a last
        /// pair dissolves that group entirely (other groups are untouched).</summary>
        public static void StoreRemove(string stable)
        {
            string g = GroupOfStable(stable);
            if (string.IsNullOrEmpty(g)) return;
            _groups[g].Remove(stable);
            if (_groups[g].Count < 2) _groups.Remove(g);
        }

        /// <summary>HOST, session boundary only (new world / clear-before-apply at manifest restore) —
        /// mirrors GrantSync.ResetStore's lifecycle exactly.</summary>
        public static void ResetStore() => _groups.Clear();

        // ── Runtime (PlayerId space, replicated, online-only) ─────────────────
        private static Dictionary<string, string> _groupByPid = new();      // online pid → groupId
        private static Dictionary<string, MergerGroupInfo> _groupInfo = new();
        private static bool _wasMember;

        /// <summary>Local UI state: an incoming proposal awaiting my Accept/Decline (proposer's pid).</summary>
        public static string IncomingFromPid = "";
        /// <summary>Local UI state: my outgoing proposal ("Cancel offer" chip until answered/withdrawn).</summary>
        public static string OutgoingToPid = "";

        public static bool IAmMember => _groupByPid.ContainsKey(MPConfig.PlayerId);
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

        /// <summary>THE enforcement union read by GrantSync.IsGranted: two distinct online players in
        /// the SAME group hold every key to each other's world.</summary>
        public static bool MergedRuntime(string a, string b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a != b
               && _groupByPid.TryGetValue(a, out var ga) && _groupByPid.TryGetValue(b, out var gb) && ga == gb;

        /// <summary>ALL machines (host applies its own build; clients apply the broadcast). MAIN THREAD
        /// (may toast). Diff-based membership toasts so formation and dissolution are both announced.</summary>
        public static void ApplyState(MergerStatePayload p)
        {
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
            _groupByPid = byPid; _groupInfo = info;
            bool now = IAmMember;
            if (now && !_wasMember)
            {
                PassengerHud.Toast("Company merger active.");
                IncomingFromPid = ""; OutgoingToPid = "";   // consumed
            }
            else if (!now && _wasMember)
                PassengerHud.Toast("Merger dissolved.");
            _wasMember = now;
        }

        /// <summary>SCENE-scoped reset (mirrors GrantSync.ResetSceneState): runtime + local UI state die
        /// with the scene and rebuild from the store (host) or the host broadcast (client). The durable
        /// store's lifecycle is session boundaries, not scene transitions.</summary>
        public static void ResetSceneState()
        {
            _groupByPid = new Dictionary<string, string>();
            _groupInfo  = new Dictionary<string, MergerGroupInfo>();
            _wasMember = false;
            IncomingFromPid = ""; OutgoingToPid = "";
        }
    }
}
