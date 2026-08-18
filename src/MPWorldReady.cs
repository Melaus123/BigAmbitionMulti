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
        private static bool  _rawWasTrue;
        private static readonly System.Collections.Generic.Dictionary<string, float> _nextDeferLogAt = new();

        internal static bool IsSettled
        {
            get
            {
                // Sweep item 3a: the raw terms are captured OUTSIDE the try so the moment
                // readiness breaks we can log WHICH term broke, with its value AT THE BREAK
                // FRAME.  Field sessions logged hundreds of deferrals whose (later-sampled)
                // state read all-green — a one-frame blip restarts the 3s settle clock and
                // was invisible; this edge line is the measurement that names the blip.
                bool raw; bool loading = false; int regs = -1; bool roleReady = false; string exMsg = "";
                try
                {
                    try { loading = UI.Load.LoadScene.isLoading; } catch { }
                    var gi = SaveGameManager.Current;
                    regs = gi?.BuildingRegistrations?.Count ?? -1;
                    roleReady = MPServer.IsRunning
                        || (MPClient.IsClientInWorld && !MPClient.IsJoinQuiescing);
                    raw = !loading && regs > 0 && roleReady;
                }
                catch (System.Exception ex) { raw = false; exMsg = ex.Message; }
                if (!raw)
                {
                    if (_rawWasTrue)
                        Plugin.Logger.LogInfo($"[Settle] readiness BROKE — loading={loading} regs={regs} roleReady={roleReady} " +
                            $"(host={MPServer.IsRunning} clientInWorld={MPClient.IsClientInWorld} quiesce={MPClient.IsJoinQuiescing})" +
                            $"{(exMsg.Length > 0 ? $" ex='{exMsg}'" : "")} — the {SettleSeconds:0}s settle clock restarts (sweep 3a).");
                    _rawWasTrue = false;
                    _rawTrueSince = -1f;
                    return false;
                }
                _rawWasTrue = true;
                if (_rawTrueSince < 0f) _rawTrueSince = Time.unscaledTime;
                return Time.unscaledTime - _rawTrueSince >= SettleSeconds;
            }
        }

        /// <summary>Round-188: readiness for MATERIALIZING world objects (ghost vehicles, traffic,
        /// puppets) — the save is settled AND the local player exists in the scene.  Streaming
        /// appliers DROP when false: the next packet re-delivers within ~0.1-10s, so a drop is
        /// recurrence-covered under the retry contract (no queue needed).</summary>
        internal static bool CanMaterialize
        {
            get
            {
                if (!IsSettled) return false;
                try { return InstanceBehavior<GameManager>.Instance?.playerController != null; }
                catch { return false; }
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
                    // Sweep 3a: print the terms the predicate ACTUALLY uses (the old line
                    // showed client-only fields on hosts — a decoy) plus how far into the
                    // settle window we are, so "all green but still deferred" reads as the
                    // re-settle window it is instead of a mystery.
                    bool loading = false; try { loading = UI.Load.LoadScene.isLoading; } catch { }
                    int regs = -1; try { regs = SaveGameManager.Current?.BuildingRegistrations?.Count ?? -1; } catch { }
                    float settledFor = _rawTrueSince < 0f ? -1f : Time.unscaledTime - _rawTrueSince;
                    Plugin.Logger.LogInfo($"[Settle] '{action}' deferred — world not settled (loading={loading} regs={regs} " +
                        $"host={MPServer.IsRunning} clientInWorld={MPClient.IsClientInWorld} quiesce={MPClient.IsJoinQuiescing} " +
                        $"rawTrueFor={settledFor:0.0}s/{SettleSeconds:0}s). Will retry.");
                }
            }
            catch { }
            return false;
        }
    }
}
