using System;
using System.Collections.Generic;
using Buildings;            // BuildingRegistration
using Extensions;           // ToShortCurrencyFormat (the native page's own formatter)
using Localizor;            // .Localize — the native "bizman_estimated_valuation" template

namespace BigAmbitionsMP
{
    /// <summary>H-BIZ-1 (user option A, 2026-09-03): the estimate shown on another PLAYER's shop page. The game has no
    /// valuation for a player-run business (an income history exists only for rival-stamped shops; personal wealth
    /// counts no businesses), so the only native figure is the closure value — every interior item's selling price,
    /// vehicles assigned to the address, and the deposit (BizManPresentation.OnTerminateContractConfirm). The OWNER
    /// computes it on request and answers the ONE viewer; the viewer caches it per address and repaints the open page.
    /// Same shape as the shared-shop sales-history route (SharedShopPrices.RequestHistory / OwnerAnswerHistory).</summary>
    public static class ShopValuation
    {
        private const string Tag = "[Valuation]";
        private static readonly Dictionary<string, float> _known   = new();   // addressKey → the owner's last answer
        private static readonly Dictionary<string, float> _askedAt = new();   // addressKey → unscaled time of the last request
        private const float ReaskSeconds = 10f;                                // once per page open, at most every 10 s per shop

        public static void Reset() { _known.Clear(); _askedAt.Clear(); }

        /// <summary>The owner's last answer for this shop; 0 until one has arrived.</summary>
        internal static float KnownValue(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _known.TryGetValue(addressKey, out var v) ? v : 0f;

        /// <summary>VIEWER: ask the owner (throttled per shop). A host viewer routes locally.</summary>
        internal static void Request(string addressKey)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey)) return;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                float now = UnityEngine.Time.unscaledTime;
                if (_askedAt.TryGetValue(addressKey, out var at) && now - at < ReaskSeconds) return;
                var p = new ShopValuationPayload { PlayerId = MPConfig.PlayerId, Action = "request", AddressKey = addressKey };
                if (MPServer.IsRunning) MPServer.HostRouteShopValuation(p, MPConfig.PlayerId);
                else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.ShopValuation, MPConfig.PlayerId, p));
                else return;   // review #6: an offline MP save has nobody to ask — no send, no claim in the log
                _askedAt[addressKey] = now;   // review r4 #6: stamped only after a real send
                Plugin.Logger.LogInfo($"{Tag} page opened on another player's shop '{addressKey}' — asking its owner for the estimate.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} request: {ex.Message}"); }
        }

        /// <summary>MAIN THREAD, both roles: a "request" reaches the owner, an "answer" reaches the viewer.</summary>
        public static void Handle(ShopValuationPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                if (p.Action == "request") OwnerAnswer(p);
                else if (p.Action == "answer") ApplyAnswer(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} handle: {ex.Message}"); }
        }

        /// <summary>The game's closure figure for one of THIS machine's own shops (0 when the shop is not ours).</summary>
        internal static float ComputeOwnValue(BuildingRegistration reg, out int items, out int vehicles)
        {
            items = 0; vehicles = 0;
            if (reg == null || !MergerFlip.TrulyMine(reg)) return 0f;
            float total = 0f;
            try
            {
                if (reg.itemInstances != null)
                    foreach (var ii in reg.itemInstances.Values)
                    {
                        if (ii == null) continue;
                        try { total += ii.GetSellingPrice(); items++; } catch { }
                    }
            }
            catch { }
            try
            {
                var vi = SaveGameManager.Current?.VehicleInstances;
                if (vi != null)
                    foreach (var v in vi)
                    {
                        if (v == null) continue;
                        bool here = false; try { here = v.Address == reg.Address; } catch { }
                        if (!here) continue;
                        try { total += v.GetSellingPrice(); vehicles++; } catch { }
                    }
            }
            catch { }
            if (items + vehicles > 0) { try { total += reg.lastDeposit; } catch { } }   // review #9: native adds the deposit only when items or vehicles exist (OnTerminateContractConfirm)
            return total < 0f ? 0f : total;
        }

        private static void OwnerAnswer(ShopValuationPayload req)
        {
            var reg = GameStatePatcher.FindRegistration(req.AddressKey);
            if (reg == null || !MergerFlip.TrulyMine(reg)) return;                 // never answer for a shop we do not run
            float value = ComputeOwnValue(reg, out int items, out int vehicles);
            var reply = new ShopValuationPayload
            {
                PlayerId = MPConfig.PlayerId, Action = "answer", AddressKey = req.AddressKey, ToPid = req.PlayerId,
                Value = value, Items = items, Vehicles = vehicles,
            };
            Plugin.Logger.LogInfo($"{Tag} answering '{req.PlayerId}' for '{req.AddressKey}': ${value:F0} ({items} item(s), {vehicles} vehicle(s), deposit included).");
            if (MPServer.IsRunning) MPServer.HostRouteShopValuation(reply, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.ShopValuation, MPConfig.PlayerId, reply));
        }

        private static void ApplyAnswer(ShopValuationPayload p)
        {
            if (p.Value < 0f || !MPServer.IsSaneMoney(p.Value, 1_000_000_000f))
            { Plugin.Logger.LogWarning($"{Tag} answer for '{p.AddressKey}' rejected: implausible value {p.Value}."); return; }
            if (!_askedAt.ContainsKey(p.AddressKey)) { Plugin.Logger.LogInfo($"{Tag} unsolicited answer for '{p.AddressKey}' ignored (review #8)."); return; }
            _known[p.AddressKey] = p.Value;
            Plugin.Logger.LogInfo($"{Tag} owner's estimate for '{p.AddressKey}': ${p.Value:F0} — repainting the page if it is open.");
            RepaintIfOpen(p.AddressKey, p.Value);
        }

        /// <summary>Ruling 32 (live parity): the page opened before the answer arrived — put the figure into the two
        /// native labels exactly as SetAiOwned does, through the game's own localized template.</summary>
        private static void RepaintIfOpen(string addressKey, float value)
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var page = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                if (page == null || !page.gameObject.activeInHierarchy) return;
                var reg = page.buildingRegistration;
                if (reg == null || GameStateReader.AddressKey(reg) != addressKey) return;
                var pres = page.GetComponentInChildren<BizManPresentation>(true);
                if (pres == null) { Plugin.Logger.LogInfo($"{Tag} page for '{addressKey}' is open but its presentation tab was not found — the figure shows on the next open."); return; }
                string valuation = value.ToShortCurrencyFormat();
                var data = "bizman_estimated_valuation".Localize(new { valuation });
                try { pres.businessSideInfoValuation?.SetData(data); } catch { }
                try { pres.businessSideOfferValuation?.SetData(data); } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} repaint: {ex.Message}"); }
        }
    }
}
