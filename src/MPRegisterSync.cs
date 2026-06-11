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
    /// register looks unstaffed, so a visiting player gets nothing.
    /// Duty mirrors the game's own Work mechanic (user spec): the owner clicks
    /// their register, picks Work, a WorkActivity runs, we broadcast ON duty;
    /// stopping work broadcasts OFF.  No keybinds anywhere.  Buying stays the
    /// NATIVE flow (pick items, click register, walk up, pay); the duty map
    /// exists solely so the future staffed-gate patch can answer 'is a player
    /// working this register' on the customer's machine.
    /// Registers are keyed by rounded world position (stable across peers --
    /// interiors are host-snapshot replicas at identical coordinates;
    /// verified cross-machine 2026-06-11: '900:0:-4' matched on both).
    /// </summary>
    public static class MPRegisterSync
    {
        // posKey → cashier playerId
        private static readonly Dictionary<string, (string playerId, Vector3 pos)> _cashiers = new();

        public static void Reset() { _cashiers.Clear(); _onDuty = false; CurrentShopOwner = ""; CurrentShopAddress = ""; }

        // ── Current building context (set by the building entry patch) ────────
        // After the rival-translation, businessOwnerRivalId holds the OWNING
        // PLAYER's id for player businesses (a real rival GUID for AI ones —
        // the host validates against the lobby roster, so AI ids fall out).
        public static string CurrentShopOwner   { get; private set; } = "";
        public static string CurrentShopAddress { get; private set; } = "";

        public static void SetCurrentShop(string ownerId, string address)
        {
            CurrentShopOwner = ownerId ?? "";
            CurrentShopAddress = address ?? "";
        }

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

        private static Controllers.CashRegisterController? FindNearestRegister(Vector3 from, float maxDist)
        {
            Controllers.CashRegisterController? best = null;
            float bestD2 = maxDist * maxDist;
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Controllers.CashRegisterController>());
            if (arr != null)
                foreach (var o in arr)
                {
                    var c = o.TryCast<Controllers.CashRegisterController>();
                    if (c == null) continue;
                    float d2 = (c.transform.position - from).sqrMagnitude;
                    if (d2 < bestD2) { bestD2 = d2; best = c; }
                }
            return best;
        }

        // ── Duty: driven by the NATIVE Work mechanic (user spec) — click your
        // register → "Work" → the game runs a WorkActivity; we mirror that
        // activity into the duty map.  Stop working (any way the game allows)
        // → off duty.  No extra keybind for the cashier.
        private static float _nextDutyAt;
        private static bool _onDuty;
        private static Vector3 _dutyPos;

        public static void TickDuty()
        {
            if (Time.unscaledTime < _nextDutyAt) return;
            _nextDutyAt = Time.unscaledTime + 1f;
            try
            {
                bool working = MPRestSync.CurrentActivityName() == "Work";
                if (working && !_onDuty)
                {
                    var ch = Helpers.PlayerHelper.PlayerController?.Character?.transform;
                    if (ch == null) return;
                    var reg = FindNearestRegister(ch.position, 5f);
                    if (reg == null) return;   // working some other station — not register duty
                    _onDuty = true;
                    _dutyPos = reg.transform.position;
                    SendToggle(_dutyPos, true);
                }
                else if (!working && _onDuty)
                {
                    _onDuty = false;
                    SendToggle(_dutyPos, false);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Register] duty: {ex.Message}"); }
        }

        /// <summary>Is the register at this world position staffed by another
        /// player?  This is the duty map's ONE consumer-facing question — the
        /// future staffed-gate patch asks it so the NATIVE customer flow
        /// (pick items → click register → walk up → pay) passes when a player
        /// is the cashier.  (The F4 buy bypass was removed — user: buying must
        /// work the way the game normally does it.)</summary>
        public static bool IsStaffedByOtherPlayer(Vector3 registerPos)
            => _cashiers.TryGetValue(Key(registerPos), out var e) && e.playerId != MPConfig.PlayerId;

        private static void SendToggle(Vector3 pos, bool on)
        {
            var p = new RegisterCashierPayload
            { PlayerId = MPConfig.PlayerId, X = pos.x, Y = pos.y, Z = pos.z, On = on };
            Apply(p);   // local echo first — instant feedback
            var env = MessageEnvelope.Create(MessageType.RegisterCashier, MPConfig.PlayerId, p);
            if (MPServer.IsRunning) MPServer.BroadcastAny(env);
            else MPClient.SendEnvelope(env);
        }

    }
}
