using System;
using Streets;   // Address.ToFormattedString extension

namespace BigAmbitionsMP
{
    /// <summary>Round-204b — host-arbitrated AI-business takeover (the round-196 pattern,
    /// as the user predicted).
    ///
    /// Why arbitration: rivals are HOST-authoritative. The first relay attempt (round-204,
    /// rig-refuted same day) let the CLIENT execute the native takeover optimistically and
    /// notified the host through the rent pipeline — which correctly refused ("occupied"),
    /// and the denial rollback then vacated the natively-taken-over business, leaving the
    /// client's copy empty and diverged. Nothing local may happen before the host says yes.
    ///
    /// Flow: client's offer click (SendOvertakeOffer prefix, AI target) → local parity
    /// checks (diplomas / amount / money — the same toasts native shows) → TakeoverRequest
    /// → host validates against LIVE data (full valuation × accept rate — the synced
    /// display valuation is advisory only, never the authority: the $0-sale exploit) and
    /// on accept writes ledger + tenant reflect FIRST → TakeoverResult → only then does
    /// the client charge the offer and run the native takeover. Deny carries the host's
    /// minimum so the toast reads as information. No reply in 6s (old host / drop) →
    /// error toast, nothing charged, nothing changed.</summary>
    public static class MPTakeover
    {
        // ── client: one pending offer at a time ─────────────────────────────────
        private static string _pendingAddr = "";
        private static float  _pendingOffer;
        private static float  _pendingSentAt;
        private const  float  ReplyTimeout = 6f;

        // Round-204c: an ACCEPTED offer whose native claim is deferred until the
        // host-furnished interior lands (the claim must run against the furnished
        // registration — running it bare was the "empty business" rig failure).
        private static string _claimAddr = "";
        private static float  _claimOffer;
        private static int    _claimItems;
        private static float  _claimDeadline;
        private static string _claimStaffJson = "";
        private const  float  ClaimTimeout = 10f;

        /// <summary>True while this machine executes the native claim — gates the
        /// AutoFillSchedule(null) NRE skip (shared with round-196's transfer flag).</summary>
        public static bool ClaimInProgress { get; private set; }

        public static void Reset() { _pendingAddr = ""; _claimAddr = ""; ClaimInProgress = false; }

        /// <summary>Client-side offer click for an AI-run business. Returns false
        /// (block native) in MP; true = let native run (single-player safety net).</summary>
        public static bool ClientOfferPrefix(BuildingRegistration reg, TMPro.TMP_InputField offerField)
        {
            try
            {
                if (!MPClient.IsConnected) return true;
                string addr = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(addr)) return true;

                // Parity checks — the same refusals native's own flow would toast.
                if (!EducationHelper.HasCompletedDiploma(DiplomaName.Headquarters))
                { Toast(UI.Notification.NotificationType.Error, "You need the Headquarters diploma to take over a business."); return false; }
                if (reg.GetBuildingType() == "ba:buildingtype_office" && !EducationHelper.HasCompletedDiploma(DiplomaName.OfficeBusinesses))
                { Toast(UI.Notification.NotificationType.Error, "You need the Office Businesses diploma to take over an office business."); return false; }
                if (!float.TryParse(offerField?.text, out float offer) || offer <= 0f)
                { Toast(UI.Notification.NotificationType.Error, "Invalid amount."); return false; }
                if (SaveGameManager.Current.Money < offer)
                { Toast(UI.Notification.NotificationType.Error, "Insufficient funds."); return false; }
                if (!string.IsNullOrEmpty(_pendingAddr))
                { Toast(UI.Notification.NotificationType.Info, "Your previous offer is still being processed."); return false; }

                _pendingAddr   = addr;
                _pendingOffer  = offer;
                _pendingSentAt = UnityEngine.Time.unscaledTime;
                MPClient.SendTakeoverRequest(addr, offer);
                Plugin.Logger.LogInfo($"[Takeover] offer ${offer:N0} for '{addr}' sent to host for arbitration (round-204b).");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Takeover] offer prefix: {ex.Message}");
                return false;   // never fall through to the client-local native flow in MP
            }
        }

        /// <summary>Client: host's verdict arrived. MAIN THREAD.</summary>
        public static void ClientHandleResult(TakeoverPayload res)
        {
            try
            {
                if (res == null || res.AddressKey != _pendingAddr)
                {
                    Plugin.Logger.LogWarning($"[Takeover] result for '{res?.AddressKey}' ignored — no matching pending offer.");
                    return;
                }
                float offer = _pendingOffer;
                _pendingAddr = "";

                var reg = GameStatePatcher.FindRegistration(res.AddressKey);
                if (reg == null) { Plugin.Logger.LogWarning($"[Takeover] no reg for '{res.AddressKey}' at confirm."); return; }

                if (!res.Accepted)
                {
                    Toast(UI.Notification.NotificationType.Error,
                        res.MinPrice > 0f ? $"Offer rejected — the owner wants at least {res.MinPrice:C0}."
                                          : "Offer rejected.");
                    Plugin.Logger.LogInfo($"[Takeover] host DENIED '{res.AddressKey}' (min ${res.MinPrice:N0}).");
                    return;
                }

                // Accepted: the host has ledgered + reflected AND furnished the interior
                // (round-204c) — its snapshot is in flight or already applied. Defer the
                // native claim until those items have landed so AddToPlayer & friends run
                // against the furnished registration, then execute from Tick().
                _claimAddr      = res.AddressKey;
                _claimOffer     = offer;
                _claimItems     = res.ItemCount;
                _claimStaffJson = res.AiEmployeesJson ?? "";
                _claimDeadline  = UnityEngine.Time.unscaledTime + ClaimTimeout;
                Plugin.Logger.LogInfo($"[Takeover] host ACCEPTED '{res.AddressKey}' — waiting for the furnished interior ({res.ItemCount} item(s)) before the native claim.");
                TryExecuteClaim();   // may already be satisfied (snapshot ordered ahead of the result)
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover] result: {ex.Message}"); }
        }

        /// <summary>Client: run the native claim once the furnished interior has landed
        /// (or the deadline passes — claim anyway rather than strand the ledger).</summary>
        private static void TryExecuteClaim()
        {
            if (string.IsNullOrEmpty(_claimAddr)) return;
            var reg = GameStatePatcher.FindRegistration(_claimAddr);
            if (reg == null) { _claimAddr = ""; return; }
            int have = 0; try { have = reg.itemInstances?.Count ?? 0; } catch { }
            bool ready = _claimItems <= 0 || have >= _claimItems
                         || UnityEngine.Time.unscaledTime >= _claimDeadline;
            if (!ready) return;
            if (have < _claimItems)
                Plugin.Logger.LogWarning($"[Takeover] claiming '{_claimAddr}' with {have}/{_claimItems} item(s) — interior snapshot incomplete at deadline.");

            string addr = _claimAddr; float offer = _claimOffer; string staffJson = _claimStaffJson;
            _claimAddr = ""; _claimStaffJson = "";

            // Round-204e: install the host-shipped AI staff data so the native claim's
            // GenerateEmployees mints real employees on THIS (the owner's) machine —
            // native fidelity: hire day, satisfaction and sick-day rolls all local.
            int staffRecords = 0;
            try
            {
                if (!string.IsNullOrEmpty(staffJson))
                {
                    var dtos = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<AiStaffDto>>(staffJson);
                    if (dtos != null && dtos.Count > 0)
                    {
                        reg.aiEmployees ??= new System.Collections.Generic.List<Buildings.BuildingTypes.Shared.AiBusinessEmployeeData>();
                        reg.aiEmployees.Clear();
                        foreach (var d in dtos)
                        {
                            // Constructor-free: both native ctors run random re-rolls that
                            // would corrupt the shipped values. Uninitialized instance +
                            // direct public-field writes; the id deterministically seeds
                            // name/gender/age in the native conversion, so fidelity is full.
                            var inst = (Buildings.BuildingTypes.Shared.AiBusinessEmployeeData)
                                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                                    typeof(Buildings.BuildingTypes.Shared.AiBusinessEmployeeData));
                            inst.id = d.Id;
                            inst.primarySkillName = d.Skill;
                            inst.primarySkillValue = d.SkillValue;
                            inst.hoursPerWeekDemandName = d.Hours;
                            inst.aiAddress = reg.Address;   // all records belong to this shop
                            inst.isPoached = false;
                            reg.aiEmployees.Add(inst);
                        }
                        staffRecords = reg.aiEmployees.Count;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover] staff install: {ex.Message}"); }

            // Charge. force:true — money was verified at request time; sub-second drift
            // must not strand a host-side ledger entry with no local claim.
            try
            {
                var data = new System.Collections.Generic.Dictionary<string, string>
                    { { "address", reg.Address.ToFormattedString() } };
                GameManager.ChangeMoneySafe(-offer, new TransactionInfo("ba:transaction_deposit", data), null, reg.Address, force: true, showNotification: true);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover] charge: {ex.Message}"); }

            // Native claim. ClaimInProgress gates the AutoFillSchedule(null) NRE skip
            // (the round-196 landmine — OvertakeBusiness's tail calls it with a null UI
            // object); we run the REGISTRATION-level auto-filler ourselves right after,
            // which is the same scheduling native's UI wrapper performs.
            ClaimInProgress = true;
            try { BizManPresentation.OvertakeBusiness(reg); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover] native claim threw mid-tail (continuing): {ex.Message}"); }
            finally { ClaimInProgress = false; }
            try { Helpers.ScheduleAutoFillerHelper.AutoFillSchedule(reg, null, null, warnIfUnassigned: false, fast: false, inhibitSuccessNotification: true); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover] auto-fill schedule: {ex.Message}"); }

            Toast(UI.Notification.NotificationType.Success, $"You took over {reg.BusinessName}!");
            Plugin.Logger.LogInfo($"[Takeover] CONFIRMED '{addr}' for ${offer:N0} — native claim executed against {have} item(s), {staffRecords} staff record(s) minted.");
        }

        /// <summary>Client: expire a pending offer that got no reply (older host that
        /// doesn't know the message, or a drop). Called from the canvas pre-block.</summary>
        public static void Tick()
        {
            try
            {
                TryExecuteClaim();   // round-204c: deferred claim waiting on the furnished interior
                if (string.IsNullOrEmpty(_pendingAddr)) return;
                if (UnityEngine.Time.unscaledTime - _pendingSentAt < ReplyTimeout) return;
                Plugin.Logger.LogWarning($"[Takeover] no reply for '{_pendingAddr}' after {ReplyTimeout:F0}s — host may be on an older mod version. Nothing was charged.");
                Toast(UI.Notification.NotificationType.Error, "No response from the host — they may need to update the mod. Nothing was charged.");
                _pendingAddr = "";
            }
            catch { _pendingAddr = ""; }
        }

        // ── host: arbitrate against live authoritative data. MAIN THREAD. ───────
        public static void HostHandleRequest(string senderPid, TakeoverPayload req)
        {
            try
            {
                if (req == null || string.IsNullOrEmpty(req.AddressKey)) return;
                var reg = GameStatePatcher.FindRegistration(req.AddressKey);
                string deny = null;
                float minPrice = 0f;

                if (reg == null) deny = "unknown address";
                else
                {
                    string bizOwner = reg.businessOwnerRivalId?.ToString() ?? "";
                    if (string.IsNullOrEmpty(bizOwner)) deny = "no business here";
                    else if (GameStatePatcher.IsSessionPlayerId(bizOwner)) deny = $"run by player '{bizOwner}'";
                    else if (reg.RentedByPlayer) deny = "the host runs this business";
                    else if (MPServer.BuildingOwners.TryGetValue(req.AddressKey, out var cur)
                             && !string.IsNullOrEmpty(cur) && cur != senderPid) deny = $"ledgered to '{cur}'";
                    else
                    {
                        // The LIVE accept check — full valuation (host has the real
                        // incomes) × the rival's accept rate, same math native runs.
                        try
                        {
                            minPrice = Helpers.CompetitionHelper.CalculateAiOwnedValuation(reg)
                                       * BigAmbitions.Rivals.RivalsHelper.GetOvertakeBusinessAcceptRate(bizOwner, reg.Address);
                        }
                        catch (Exception ex)
                        {
                            deny = "valuation unavailable";
                            Plugin.Logger.LogWarning($"[Takeover] host valuation for '{req.AddressKey}': {ex.Message}");
                        }
                        if (deny == null && req.OfferAmount < minPrice) deny = "offer below minimum";
                    }
                }

                if (deny != null)
                {
                    Plugin.Logger.LogInfo($"[Takeover] DENIED '{req.AddressKey}' from '{senderPid}' (${req.OfferAmount:N0}): {deny} (min ${minPrice:N0}).");
                    MPServer.SendHubTo(senderPid, MessageType.TakeoverResult,
                        new TakeoverPayload { AddressKey = req.AddressKey, Accepted = false, MinPrice = minPrice });
                    return;
                }

                // Accept: authority FIRST (ledger + tenant reflect — keeps the business
                // fields intact, detaches it from rival machinery).
                MPServer.BuildingOwners[req.AddressKey] = senderPid;
                GameStatePatcher.HostReflectPlayerRent(req.AddressKey, senderPid);
                try { reg.takenOver = true; } catch { }   // mirror native AddToPlayer's stamp on the authoritative copy
                MPServer.RefreshBuildingAccess();

                // Round-204c FURNISH — the rig's "empty business" failure: an AI shop's
                // furniture is ephemeral scene dressing from the layout set; REAL item
                // instances are created only by AddToPlayer's InsertBusinessLayoutSet at
                // takeover — and the layout CATALOG never loads on clients (same absence
                // that broke the client valuation). The HOST converts, then ships the
                // furnished interior AHEAD of the confirm.
                HostFurnishClaimedShop(reg, req.AddressKey);
                int itemCount = 0;
                try { itemCount = reg.itemInstances?.Count ?? 0; } catch { }
                try { InteriorSync.SendSnapshotToPlayer(req.AddressKey, senderPid, forceItemAuthority: true); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover] interior send: {ex.Message}"); }

                // Round-204e STAFF — GenerateEmployees converts reg.aiEmployees (host-side
                // only, never syncs) into real staff; the client's list is empty, so its
                // claim minted nobody. Ship the data with the confirm; the host's copy is
                // cleared (they're the buyer's to mint — mirrors native's clear-on-convert).
                // Shipped as a flat DTO — the native type has only side-effectful
                // parameterized constructors (random re-rolls), so it can neither be
                // deserialized by Newtonsoft nor rebuilt through a constructor without
                // corrupting the shipped values (204e first attempt, rig-refuted).
                string aiStaffJson = "";
                int aiStaffCount = 0;
                try
                {
                    var aiList = reg.aiEmployees;
                    if (aiList != null && aiList.Count > 0)
                    {
                        var dtos = new System.Collections.Generic.List<AiStaffDto>();
                        int skippedPoached = 0;
                        foreach (var a in aiList)
                        {
                            if (a == null) continue;
                            // Poached records resolve through a machine-local employee
                            // lookup — they cannot travel. Rare; logged.
                            if (a.isPoached) { skippedPoached++; continue; }
                            dtos.Add(new AiStaffDto { Id = a.id, Skill = a.primarySkillName, SkillValue = a.primarySkillValue, Hours = a.hoursPerWeekDemandName });
                        }
                        aiStaffCount = dtos.Count;
                        if (dtos.Count > 0) aiStaffJson = Newtonsoft.Json.JsonConvert.SerializeObject(dtos);
                        if (skippedPoached > 0) Plugin.Logger.LogWarning($"[Takeover] {skippedPoached} poached staff record(s) not shipped (machine-local reference).");
                        aiList.Clear();
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover] staff serialize: {ex.Message}"); }

                Plugin.Logger.LogInfo($"[Takeover] ACCEPTED '{req.AddressKey}' from '{senderPid}' for ${req.OfferAmount:N0} (min was ${minPrice:N0}) — ledgered + reflected + furnished ({itemCount} item(s)) + {aiStaffCount} staff record(s); confirm sent.");
                MPServer.SendHubTo(senderPid, MessageType.TakeoverResult,
                    new TakeoverPayload { AddressKey = req.AddressKey, Accepted = true, ItemCount = itemCount, AiEmployeesJson = aiStaffJson });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover] host arbitration: {ex.Message}"); }
        }

        /// <summary>HOST: convert a claimed ex-AI shop's layout blueprint into real item
        /// instances — the takeover furnish. Buyer semantics: RentedByPlayer is briefly
        /// true around the insert (it gates the seasonal-item takeover swap and the
        /// purchaser-panel disable, exactly what native does on the buyer's machine);
        /// the blueprint reference is cleared ONLY on success, so a failed furnish
        /// stays retryable. Returns true when items were inserted. MAIN THREAD.</summary>
        public static bool HostFurnishClaimedShop(BuildingRegistration reg, string addressKey)
        {
            try
            {
                string layout = reg.Layout;
                if (string.IsNullOrEmpty(layout)) return false;   // already furnished (or never an AI layout)
                var set = BusinessLayoutSets.BusinessLayoutSetHelper.GetOrLoadBusinessLayoutSet(
                              reg.businessTypeName, new Blueprints.BuildingSizeInfo(reg.BuildingCached), layout.ToLower(), warnIfNotFound: false);
                if (set == null)
                {
                    Plugin.Logger.LogWarning($"[Takeover] no layout set for '{addressKey}' ({reg.businessTypeName} '{layout}') — furnish skipped (will retry at the owner's next join).");
                    return false;
                }
                bool priorRented = reg.RentedByPlayer;
                reg.RentedByPlayer = true;   // buyer semantics for the insert ONLY
                try { BusinessLayoutSets.BusinessLayoutSetHelper.InsertLayoutSet(reg, set, shouldRandomlyFillShelves: true); }
                finally { reg.RentedByPlayer = priorRented; }
                reg.Layout = null;
                Plugin.Logger.LogInfo($"[Takeover] furnished '{addressKey}' from layout '{layout}' — {reg.itemInstances?.Count ?? 0} item(s).");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover] furnish '{addressKey}': {ex.Message}"); return false; }
        }

        /// <summary>HOST, world-ready heal (user-approved round-204d): a business claimed
        /// by the JOINING player whose registration still carries a layout blueprint is a
        /// takeover whose furnish never completed (a completed furnish always clears the
        /// blueprint) — the rig's pre-fix Kabob's, or a future accept whose furnish
        /// failed. Furnish it now and ship the forced snapshot to the owner. Runs per
        /// join, so failures retry (recurrence-covered) and success self-disarms.</summary>
        public static void HostHealUnfurnishedShopsFor(string pid)
        {
            try
            {
                if (!MPServer.IsRunning) return;
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return;
                foreach (var reg in regs)
                {
                    try
                    {
                        if (reg == null || reg.RentedByPlayer) continue;                       // host's own: native handled it
                        if ((reg.businessOwnerRivalId?.ToString() ?? "") != pid) continue;     // not this joiner's shop
                        if (string.IsNullOrEmpty(reg.Layout)) continue;                        // furnished — nothing to heal
                        if (reg.businessTypeName == "ba:businesstype_empty") continue;         // no business to furnish (fresh rental shape)
                        string addr = GameStateReader.AddressKey(reg);
                        // OWNER-FURNISHED guard (user question 2026-08-01): the insert is
                        // ADDITIVE — healing a shop the owner already furnished by hand
                        // would stack the AI layout on top of their work. More items than
                        // the bare infrastructure markers ⇒ someone furnished it ⇒ skip
                        // AND clear the blueprint so this shop never re-checks.
                        int items = 0; try { items = reg.itemInstances?.Count ?? 0; } catch { }
                        if (items > 3)
                        {
                            reg.Layout = null;
                            Plugin.Logger.LogInfo($"[Takeover] '{addr}' already has {items} item(s) — owner furnished it; blueprint cleared, heal disarmed (round-204d).");
                            continue;
                        }
                        if (HostFurnishClaimedShop(reg, addr))
                        {
                            InteriorSync.SendSnapshotToPlayer(addr, pid, forceItemAuthority: true);
                            Plugin.Logger.LogWarning($"[Takeover] HEALED '{addr}' for '{pid}' — claimed shop was still unfurnished; items sent (round-204d).");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover] heal sweep: {ex.Message}"); }
        }

        /// <summary>Round-204e wire shape for one AI staff record — everything the
        /// native conversion (GetEmployeeInstance) consumes except the address, which
        /// is always the shop itself and re-attached by the receiver.</summary>
        public class AiStaffDto
        {
            public string Id = "";
            public string Skill = "";
            public float  SkillValue;
            public string Hours = "";
        }

        private static void Toast(UI.Notification.NotificationType t, string msg)
        {
            try { UI.Notification.Notifications.Show(t, msg); } catch { }
        }
    }
}
