using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>Round-179 — the single readiness authority for the "ACTING BEFORE READING YOUR OWN
    /// SAVE" bug class (see ANTIPATTERNS.md).  Five field incidents in one week were this class in
    /// different clothes: a crash-recovery save uploaded a near-empty .hsg before the world loaded;
    /// queue scores read unmaterialized item data as "empty"; arbitration nearly ruled on unknown
    /// copies; the join-time interior publish pushed all-zero registrations as the owner's truth
    /// (deleting a player's furniture on every spawn); and ownership claims raced the join-quiesce.
    /// Each got a local guard — this gate kills the class at one source of truth instead.
    ///
    /// CONTRACT (user-set, 2026-07-28): a gate deferral NEVER consumes a one-shot flag; every gated
    /// action is either tick-driven (retries until settled, then runs and marks done) or explicitly
    /// covered by its own recurrence (e.g. coordinated saves).  Deferrals LOG (throttled) so a
    /// stuck "waiting forever" state is as visible as an early fire.
    ///
    /// EXEMPTION, by design: the HOST's world-ready integrity/ledger passes run BEFORE this gate
    /// would allow — deliberately.  A host's own save load completes synchronously before
    /// world-ready fires, so its data is whole by construction (the class is about CLIENTS mid-join
    /// and processes mid-load), and those passes must run before native actors can touch a
    /// just-loaded world.</summary>
    internal static class MPWorldReady
    {
        private const float SettleSeconds = 3f;
        private static float _rawTrueSince = -1f;
        private static readonly System.Collections.Generic.Dictionary<string, float> _nextDeferLogAt = new();

        internal static bool IsSettled
        {
            get
            {
                bool raw;
                try
                {
                    bool loading = false;
                    try { loading = UI.Load.LoadScene.isLoading; } catch { }
                    var gi = SaveGameManager.Current;
                    bool worldReadable = !loading && gi?.BuildingRegistrations != null && gi.BuildingRegistrations.Count > 0;
                    bool roleReady = MPServer.IsRunning
                        || (MPClient.IsClientInWorld && !MPClient.IsJoinQuiescing);
                    raw = worldReadable && roleReady;
                }
                catch { raw = false; }
                if (!raw) { _rawTrueSince = -1f; return false; }
                if (_rawTrueSince < 0f) _rawTrueSince = Time.unscaledTime;
                return Time.unscaledTime - _rawTrueSince >= SettleSeconds;
            }
        }

        /// <summary>The gate + tripwire.  False logs a throttled deferral line naming the action —
        /// visible on the dev rig AND in field reports, so both early fires and stuck deferrals
        /// surface as log lines instead of eaten data.</summary>
        internal static bool AssertSettledFor(string action)
        {
            if (IsSettled) return true;
            try
            {
                if (!_nextDeferLogAt.TryGetValue(action, out var at) || Time.unscaledTime >= at)
                {
                    _nextDeferLogAt[action] = Time.unscaledTime + 5f;
                    bool loading = false; try { loading = UI.Load.LoadScene.isLoading; } catch { }
                    Plugin.Logger.LogInfo($"[Settle] '{action}' deferred — world not settled (loading={loading} " +
                        $"inWorld={MPClient.IsClientInWorld} quiesce={MPClient.IsJoinQuiescing} host={MPServer.IsRunning}). Will retry.");
                }
            }
            catch { }
            return false;
        }
    }
}
