using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Field 20260830-180810 (user-approved): ENTRANCE FEES at session players' venues.
    ///
    /// Native mechanism (CityBuildingController.Interact → BuildingRegistration
    /// .GetEntranceFeeForPlayer :609-616): the door charges the AI-business entry fee to
    /// everyone except the machine whose registration reads RentedByPlayer — the only
    /// relationship single-player knows. Two MP defects:
    ///  1. A granted BUSINESS HELPER or MERGER PARTNER at the venue is charged like a
    ///     stranger ("have to pay entry fee. I am listed as a business partner").
    ///  2. A non-partner session player who pays is charged into thin air — the fee is a
    ///     plain local expense; the venue's OWNER is never credited (the same
    ///     paid-but-nobody-receives family as 07-bug-classes' checkout shapes).
    ///
    /// Fix 1 (exemption): postfix the fee computation — helper/merger at a session
    /// player's venue ⇒ fee 0; the native door then enters without a dialog.
    /// Fix 2 (owner credit): the door charges inside a confirm-dialog closure we cannot
    /// patch directly, but the charge is uniquely tagged ("ba:transaction_entrancefee").
    /// The fee postfix STASHES (venue, owner, fee item, amount) whenever it returns a
    /// non-zero fee for a session player's venue; a postfix on ChangeMoneySafe matches
    /// the tagged, amount-equal charge within a short window and sends the RemoteSale.</summary>
    public static class EntranceFeeSync
    {
        private static string _stashAddr = "", _stashOwner = "", _stashFeeItem = "", _stashBizName = "";
        private static float _stashFee;
        private static float _stashAt = -999f;
        private const float StashWindowSeconds = 120f;   // door dialog can sit open a while

        [HarmonyPatch(typeof(BuildingRegistration), nameof(BuildingRegistration.GetEntranceFeeForPlayer))]
        public static class Patch_EntranceFee_PartnersExempt
        {
            static void Postfix(BuildingRegistration __instance, ref float __result)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                    // Recheck B-1: EVERY fee computation tears up the note first. Without this, a
                    // DECLINED dialog at a session player's venue left a live stash, and a
                    // same-amount fee paid at an ordinary AI venue within the window was mirrored
                    // to the stashed player — money created, credited to someone who sold nothing.
                    // The latest computation is always the venue the player is actually facing.
                    _stashOwner = ""; _stashAt = -999f;
                    if (__result <= 0f) return;
                    if (!GameStatePatcher.IsAnyPlayerBusiness(__instance)) return;   // AI venue → fully native
                    string addr = GameStateReader.AddressKey(__instance);
                    // Fix 1: partners walk in free — a Business-grant helper or a merger
                    // member is staff here, not a customer.
                    bool partner = false;
                    try { partner = GrantSync.IsHelperBusiness(addr) || MergerFlip.IsFlipped(addr); } catch { }
                    if (partner)
                    {
                        Plugin.Logger.LogInfo($"[EntranceFee] partner exemption at '{addr}' ({__instance.BusinessName}) — fee {__result:F2} → 0 (helper/merger).");
                        __result = 0f;
                        return;
                    }
                    // Fix 2 stash: a paying session player — remember exactly which venue this
                    // fee belongs to so the tagged charge can be mirrored to the owner.
                    _stashAddr = addr;
                    _stashOwner = __instance.businessOwnerRivalId ?? "";
                    _stashFee = __result;
                    _stashAt = UnityEngine.Time.unscaledTime;
                    _stashBizName = __instance.BusinessName ?? "";   // B-1 guard 2: the charge's TransactionInfo carries this
                    _stashFeeItem = "";
                    try { _stashFeeItem = Helpers.BusinessTypeHelper.GetEntranceFeeNameForBusinessType(Helpers.BusinessTypeHelper.GetData(__instance.businessTypeName)) ?? ""; } catch { }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[EntranceFee] fee postfix: {ex.Message}"); }
            }
        }

        [HarmonyPatch]
        public static class Patch_EntranceFee_OwnerCredit
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                // Every ChangeMoneySafe overload that carries a TransactionInfo — the door's
                // charge is tagged there. Yield-nothing on failure (dead, never degraded).
                System.Collections.Generic.List<System.Reflection.MethodBase> found = new();
                try
                {
                    foreach (var m in typeof(GameManager).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
                    {
                        if (m.Name != "ChangeMoneySafe") continue;
                        foreach (var p in m.GetParameters())
                            if (p.ParameterType == typeof(TransactionInfo)) { found.Add(m); break; }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[EntranceFee] target scan: {ex.Message}"); }
                if (found.Count == 0) Plugin.Logger.LogWarning("[EntranceFee] no ChangeMoneySafe(TransactionInfo) overloads found — owner-credit mirror dead.");
                return found;
            }

            static void Postfix(bool __result, float __0, TransactionInfo __1)
            {
                try
                {
                    if (!__result) return;                                        // charge refused — nothing happened
                    if (__1 == null || __1.Type != "ba:transaction_entrancefee") return;
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                    if (string.IsNullOrEmpty(_stashOwner)) return;
                    if (UnityEngine.Time.unscaledTime - _stashAt > StashWindowSeconds) return;
                    float paid = -__0;                                            // the door passes a negative amount
                    if (paid <= 0f || Math.Abs(paid - _stashFee) > 0.01f) return; // must match the stashed fee exactly
                    // Recheck B-1 guard 2: both charge sites build the TransactionInfo with
                    // {businessName} — require it to name the stashed venue, so even a stash the
                    // clear above somehow missed can never credit across venues.
                    try
                    {
                        if (__1.Data == null || !__1.Data.TryGetValue("businessName", out var bn) || bn != _stashBizName)
                        {
                            Plugin.Logger.LogInfo($"[EntranceFee] tagged charge did not name the stashed venue ('{_stashBizName}') — not mirrored.");
                            return;
                        }
                    }
                    catch { return; }
                    var sale = new RemoteSalePayload
                    {
                        BuyerId = MPConfig.PlayerId,
                        OwnerId = _stashOwner,
                        Address = _stashAddr,
                        Total   = paid,
                        Desc    = $"{(_stashFeeItem.Length > 0 ? _stashFeeItem : "entrance fee")} x1",
                    };
                    if (_stashFeeItem.Length > 0) sale.Items.Add(new SaleItem { ItemName = _stashFeeItem, Amount = 1 });
                    if (MPServer.IsRunning) MPServer.HandleRemoteSale(sale, MPConfig.PlayerId);
                    else MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.RemoteSale, MPConfig.PlayerId, sale));
                    Plugin.Logger.LogInfo($"[EntranceFee] mirrored ${paid:F2} entry fee at '{_stashAddr}' to owner '{_stashOwner}'.");
                    _stashOwner = ""; _stashAt = -999f;                           // one mirror per stash
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[EntranceFee] owner credit: {ex.Message}"); }
            }
        }
    }
}
