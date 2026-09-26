using System;
using System.Collections.Generic;
using System.Text;

namespace BigAmbitionsMP
{
    /// <summary>
    /// H-MERGERSTOCK-2 (user-approved 2026-09-23) - the per-player building ACCESS SETS' backstop, HOST only.
    ///
    /// The sets (who may enter / work in / manage which building) are computed by MPServer.BuildBuildingAccessFor and
    /// pushed by MPServer.RefreshBuildingAccess. That refresh used to run ONLY when some caller remembered to call it
    /// (rent / vacate / buy / grant / merger / join / a client's shop type arriving). Every other change of an input
    /// left the sets stale until an unrelated refresh - bundle 145749: the host rented an EMPTY building, set its
    /// business type afterwards, and its merged partner's deposit into the till there went UNROUTED and was wiped.
    ///
    /// Tick, about once a second on the main thread, fingerprints EVERY real input of BuildBuildingAccessFor:
    ///   - each operator-ledger entry (BuildingOwners) and owner-deed entry (BuildingRealEstateOwners): address, owner,
    ///     "type counts as a business", "is the headquarters" - the type read only for LEDGER addresses, through an
    ///     address->registration map rebuilt when the building list changes (FindRegistration is a linear scan);
    ///   - the grant answers the builder actually reads, for every ledger owner x every player: Business (which already
    ///     unions merger membership - GrantSync.IsGranted), Business DIRECT, Housing. That folds the live grant tables
    ///     AND merger membership into the fingerprint in the exact form the builder consumes them;
    ///   - the connected player list.
    /// A changed fingerprint calls the existing RefreshBuildingAccess("inputs") (which still pushes to everyone and
    /// writes the budgeted "[Access] sets rebuilt (inputs): ..." line when someone's sets really changed). A failed push
    /// leaves the fingerprint unchanged, so the next tick retries - at most RetryCap times per fingerprint (review L3: one
    /// player whose send keeps failing must not make the host re-push to EVERYONE every second); after that the
    /// fingerprint is taken as done (logged once) and the next REAL input change pushes again. The direct refresh calls all stay (instant effect);
    /// this tick is what makes a missed input impossible rather than merely unlikely.
    /// </summary>
    internal static class AccessSets
    {
        private const string TypeEmpty = "ba:businesstype_empty";
        private const string TypeHq    = "ba:businesstype_headquarters";

        private static float  _nextTick;
        private static string _lastSig = "";   // "" = nothing pushed by this backstop yet this session
        private static object? _regsRef;       // the building list the map below was built from
        private static int     _regsCount = -1;
        private static readonly Dictionary<string, BuildingRegistration> _regByAddr = new Dictionary<string, BuildingRegistration>(StringComparer.Ordinal);
        private static int _warned;            // warning budget, 10 per session
        private const int RetryCap = 3;        // review L3: retries of ONE fingerprint after its first failed push
        private static string _failSig = "";   // the fingerprint whose push is failing
        private static int _failRetries;       // retries spent on _failSig
        private static int _gaveUpLogged;      // "gave up" lines this session (cap 20)

        /// <summary>The ONE rule for "this business type counts as a business" - the access builder, this fingerprint
        /// and BusinessHelperRoute.HelperAt all read it, so they can never disagree. Empty / "empty" = not a business
        /// (an unfurnished rental, and every home).</summary>
        internal static bool CountsAsBusiness(string? businessTypeName)
            => !string.IsNullOrEmpty(businessTypeName) && businessTypeName != TypeEmpty;

        /// <summary>Session boundary (lobby arm / session stop - where MPServer clears its access signatures).</summary>
        internal static void Reset()
        {
            _lastSig = ""; _regsRef = null; _regsCount = -1; _warned = 0; _failSig = ""; _failRetries = 0; _gaveUpLogged = 0;
            try { _regByAddr.Clear(); } catch { }
        }

        /// <summary>HOST, MAIN THREAD, ~1 Hz (called from MergerFlip.Tick beside the host's merger push).</summary>
        internal static void Tick()
        {
            try
            {
                if (!MPServer.IsRunning) return;
                float now = UnityEngine.Time.unscaledTime;
                if (now < _nextTick) return;
                _nextTick = now + 1f;
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return;                     // no world yet - nothing to classify
                RefreshRegMap(regs);
                string sig = Fingerprint();
                if (sig == _lastSig) return;
                if (MPServer.RefreshBuildingAccess("inputs")) { _lastSig = sig; _failSig = ""; _failRetries = 0; return; }
                // Failed push -> keep the old fingerprint and retry next tick, but at most RetryCap times for THIS
                // fingerprint (review L3); then take it as done and wait for the next real input change.
                if (sig != _failSig) { _failSig = sig; _failRetries = 0; }
                else _failRetries++;
                if (_failRetries >= RetryCap)
                {
                    _lastSig = sig;
                    if (_gaveUpLogged++ < 20)   // once per fingerprint, and a session cap on top
                        Plugin.Logger.LogWarning($"[Access] backstop refresh still did not reach everyone after {RetryCap} retries - waiting for the next input change.");
                }
                else if (_warned++ < 10) Plugin.Logger.LogWarning("[Access] backstop refresh did not reach everyone - retrying next tick.");
            }
            catch (Exception ex)
            {
                if (_warned++ < 10) Plugin.Logger.LogWarning($"[Access] backstop tick: {ex.Message}");
            }
        }

        /// <summary>address -> registration, rebuilt only when the building list itself changes.</summary>
        private static void RefreshRegMap(System.Collections.IList regs)
        {
            if (ReferenceEquals(regs, _regsRef) && regs.Count == _regsCount) return;
            _regByAddr.Clear();
            foreach (var o in regs)
            {
                if (!(o is BuildingRegistration reg)) continue;
                try
                {
                    string a = GameStateReader.AddressKey(reg);
                    if (!string.IsNullOrEmpty(a)) _regByAddr[a] = reg;
                }
                catch { }
            }
            _regsRef = regs; _regsCount = regs.Count;
        }

        private static string TypeOf(string addr)
        {
            if (!_regByAddr.TryGetValue(addr, out var reg) || reg == null) return "";
            try { return reg.businessTypeName ?? ""; } catch { return ""; }
        }

        private static string Fingerprint()
        {
            string me = MPConfig.PlayerId ?? "";
            var players = MPServer.AccessPeerPids();
            if (!players.Contains(me)) players.Add(me);
            players.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder(4096);
            sb.Append("P:");
            foreach (var p in players) sb.Append(p).Append(',');
            var owners = new SortedSet<string>(StringComparer.Ordinal);
            Ledger(sb, 'O', MPServer.BuildingOwners, owners, me);
            Ledger(sb, 'R', MPServer.BuildingRealEstateOwners, owners, me);
            sb.Append("|G:");
            foreach (var o in owners)
                foreach (var p in players)
                {
                    if (o == p) continue;   // the builder skips an owner's own buildings
                    sb.Append(GrantSync.IsGranted(GrantKind.Business, o, p) ? '1' : '0')
                      .Append(GrantSync.IsGrantedDirect(GrantKind.Business, o, p) ? '1' : '0')
                      .Append(GrantSync.IsGranted(GrantKind.Housing, o, p) ? '1' : '0');
                }
            return sb.ToString();
        }

        private static void Ledger(StringBuilder sb, char tag, IEnumerable<KeyValuePair<string, string>> map, SortedSet<string> owners, string me)
        {
            var rows = new List<KeyValuePair<string, string>>(map);   // snapshot (a ConcurrentDictionary enumerates safely)
            rows.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            sb.Append('|').Append(tag).Append(':');
            foreach (var kv in rows)
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                string owner = kv.Value == "host" ? me : kv.Value;   // the builder resolves the host sentinel the same way
                owners.Add(owner);
                string bt = TypeOf(kv.Key);
                sb.Append(kv.Key).Append('=').Append(owner)
                  .Append(CountsAsBusiness(bt) ? 'B' : '-')
                  .Append(bt == TypeHq ? 'H' : '-')
                  .Append(';');
            }
        }
    }
}
