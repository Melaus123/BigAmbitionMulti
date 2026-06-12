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
    /// Duty mirrors the game's own Work mechanic (user spec): the owner clicks
    /// their register, picks Work, a WorkActivity runs, we broadcast ON duty;
    /// stopping work broadcasts OFF.  No keybinds anywhere.
    ///
    /// Buying in a duty-staffed shop: shelves/basket are native; the register
    /// click routes to the game's SELF-CHECKOUT flow (Patch_RegisterInteract_
    /// SelfCheckout) and the ORDER is finalized by the MP finalizer
    /// (Patch_MPOrderFinalizer) — charge from the synced store table, revenue
    /// + authoritative stock decrement on the owner side via RemoteSale.
    ///
    /// (The synthetic-employee staffing approach was DELETED 2026-06-12 after
    /// the self-checkout pivot verified — history and rationale in the design
    /// doc.  The native staffing evaluator refuses rival-translated shops at
    /// its first gate; no roster/WorkShift injection can reach it.)
    ///
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
                    // Worker-side price-table snapshot — cross-machine evidence
                    // pairing for any future price dispute.
                    try
                    {
                        var gi = SaveGameManager.Current;
                        if (gi != null)
                            foreach (var r in gi.BuildingRegistrations)
                                if (r != null && GameStateReader.AddressKey(r) == CurrentShopAddress)
                                { LogShopPrices(r, "[Register/prices]"); break; }
                    }
                    catch { }
                }
                else if (!working && _onDuty)
                {
                    _onDuty = false;
                    SendToggle(_dutyPos, false);
                }

                MPPatches.Patch_MPOrderFinalizer.TickPending();   // service-moment completion
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Register] duty: {ex.Message}"); }
        }

        /// <summary>Is the register at this world position staffed by another
        /// player?  The duty map's ONE consumer-facing question — gates the
        /// self-checkout routing and the queue guard.</summary>
        public static bool IsStaffedByOtherPlayer(Vector3 registerPos)
            => _cashiers.TryGetValue(Key(registerPos), out var e) && e.playerId != MPConfig.PlayerId;

        private static void SendToggle(Vector3 pos, bool on)
        {
            var p = new RegisterCashierPayload
            {
                PlayerId = MPConfig.PlayerId, X = pos.x, Y = pos.y, Z = pos.z, On = on,
                Address = CurrentShopAddress   // worker is inside the shop when toggling
            };
            Apply(p);   // local echo first — instant feedback
            var env = MessageEnvelope.Create(MessageType.RegisterCashier, MPConfig.PlayerId, p);
            if (MPServer.IsRunning) MPServer.BroadcastAny(env);
            else MPClient.SendEnvelope(env);
        }

        /// <summary>The CURRENT shop's set price for an item (synced store
        /// table — the only charge source that matters per user), or -1 when
        /// the table has no entry.</summary>
        public static float GetShopPrice(int itemNameValue)
            => GetShopPriceAt(CurrentShopAddress, itemNameValue);

        /// <summary>Same lookup for an arbitrary shop address key.</summary>
        public static float GetShopPriceAt(string addressKey, int itemNameValue)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || string.IsNullOrEmpty(addressKey)) return -1f;
                foreach (var r in gi.BuildingRegistrations)
                {
                    if (r == null || GameStateReader.AddressKey(r) != addressKey) continue;
                    var prices = r.retailPrices;
                    if (prices != null)
                        for (int i = 0; i < prices.Count; i++)
                        {
                            var rp = prices[i];
                            if (rp != null && (int)rp.itemName == itemNameValue) return rp.price;
                        }
                    return -1f;
                }
            }
            catch { }
            return -1f;
        }

        /// <summary>Dump the shop's SET price table (the only price source that
        /// matters at checkout per user — cargo pricePerUnit is NOT it).</summary>
        internal static void LogShopPrices(BuildingRegistration reg, string tag)
        {
            try
            {
                var prices = reg.retailPrices;
                if (prices == null || prices.Count == 0)
                {
                    Plugin.Logger.LogInfo($"{tag} '{GameStateReader.AddressKey(reg)}' retailPrices EMPTY (defaults not materialized).");
                    return;
                }
                var sb = new System.Text.StringBuilder($"{tag} '{GameStateReader.AddressKey(reg)}' retailPrices: ");
                for (int i = 0; i < prices.Count && i < 8; i++)
                {
                    var rp = prices[i];
                    if (rp != null) sb.Append($"{rp.itemName}=${rp.price:F2} ");
                }
                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{tag} read: {ex.Message}"); }
        }

    }
}
