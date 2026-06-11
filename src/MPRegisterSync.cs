using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Player-staffed cash registers (Wave-2; design:
    /// .modding/03-systems/cross-player-shopping.md).
    ///
    /// The game's own flows dead-end for us: an owner interacting with their
    /// register gets the WORK/management UI, and on other machines the
    /// register looks unstaffed, so a visiting player gets nothing.  So:
    ///  - F4 near a register with NO cashier → I go ON DUTY (synced to all).
    ///  - F4 again (me on duty)             → off duty.
    ///  - F4 near a register where ANOTHER player is on duty → the native
    ///    full-service order UI opens (invoked directly — bypasses the
    ///    employee-staffed gate) and the purchase runs the game's own
    ///    self-purchase path; [SalesProbe] logs it, RemoteSale (slice 1)
    ///    will route the revenue.
    /// Registers are keyed by rounded world position (stable across peers —
    /// interiors are host-snapshot replicas at identical coordinates).
    /// </summary>
    public static class MPRegisterSync
    {
        // posKey → cashier playerId
        private static readonly Dictionary<string, (string playerId, Vector3 pos)> _cashiers = new();

        public static void Reset() => _cashiers.Clear();

        private static string Key(Vector3 p)
            => $"{Mathf.RoundToInt(p.x)}:{Mathf.RoundToInt(p.y)}:{Mathf.RoundToInt(p.z)}";

        /// <summary>Apply an on/off-duty state (local echo + remote).</summary>
        public static void Apply(RegisterCashierPayload? p)
        {
            if (p == null || string.IsNullOrEmpty(p.PlayerId)) return;
            var pos = new Vector3(p.X, p.Y, p.Z);
            string k = Key(pos);
            if (p.On)
            {
                _cashiers[k] = (p.PlayerId, pos);
                Plugin.Logger.LogInfo($"[Register] '{p.PlayerId}' ON duty at {k}.");
            }
            else if (_cashiers.TryGetValue(k, out var e) && e.playerId == p.PlayerId)
            {
                _cashiers.Remove(k);
                Plugin.Logger.LogInfo($"[Register] '{p.PlayerId}' OFF duty at {k}.");
            }
        }

        /// <summary>A player disconnected — their duty posts clear.</summary>
        public static void RemovePlayer(string playerId)
        {
            var dead = new List<string>();
            foreach (var kv in _cashiers) if (kv.Value.playerId == playerId) dead.Add(kv.Key);
            foreach (var k in dead) _cashiers.Remove(k);
            if (dead.Count > 0) Plugin.Logger.LogInfo($"[Register] cleared {dead.Count} duty post(s) of departed '{playerId}'.");
        }

        /// <summary>F4 pressed in-world: toggle duty or order, by proximity.</summary>
        public static void OnF4()
        {
            try
            {
                var ch = Helpers.PlayerHelper.PlayerController?.Character?.transform;
                if (ch == null) return;

                Controllers.CashRegisterController? best = null;
                float bestD2 = 4.0f * 4.0f;
                var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Controllers.CashRegisterController>());
                if (arr != null)
                    foreach (var o in arr)
                    {
                        var c = o.TryCast<Controllers.CashRegisterController>();
                        if (c == null) continue;
                        float d2 = (c.transform.position - ch.position).sqrMagnitude;
                        if (d2 < bestD2) { bestD2 = d2; best = c; }
                    }
                if (best == null)
                {
                    Plugin.Logger.LogInfo("[Register] F4: no cash register within 4m.");
                    return;
                }

                var pos = best.transform.position;
                string k = Key(pos);
                if (_cashiers.TryGetValue(k, out var e))
                {
                    if (e.playerId == MPConfig.PlayerId) SendToggle(pos, false);     // my post → step off
                    else OpenOrderUI(best, e.playerId);                              // staffed by them → buy
                }
                else SendToggle(pos, true);                                          // free → step on
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Register] F4: {ex.Message}"); }
        }

        private static void SendToggle(Vector3 pos, bool on)
        {
            var p = new RegisterCashierPayload
            { PlayerId = MPConfig.PlayerId, X = pos.x, Y = pos.y, Z = pos.z, On = on };
            Apply(p);   // local echo first — instant feedback
            var env = MessageEnvelope.Create(MessageType.RegisterCashier, MPConfig.PlayerId, p);
            if (MPServer.IsRunning) MPServer.BroadcastAny(env);
            else MPClient.SendEnvelope(env);
        }

        private static void OpenOrderUI(Controllers.CashRegisterController reg, string cashierId)
        {
            try
            {
                var mi = typeof(Controllers.CashRegisterController).GetMethod(
                    "OpenFullServiceOrderUI",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (mi == null) { Plugin.Logger.LogWarning("[Register] OpenFullServiceOrderUI not found."); return; }
                mi.Invoke(null, new object[] { reg });
                Plugin.Logger.LogInfo($"[Register] order UI opened — cashier '{cashierId}'.");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[Register] order UI: {ex.Message} / {ex.InnerException?.Message}"); }
        }
    }
}
