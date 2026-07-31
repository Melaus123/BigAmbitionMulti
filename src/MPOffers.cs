using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Round-196: player-to-player business sale — the EXECUTION legs.
    ///
    /// The offer lifecycle (create / revoke / accept / decline / funds-at-accept
    /// enforcement / money movement) rides the existing hub offer system
    /// (MPHub, LoanOffer Kind="business"): the BUYER is the offerer and pays the
    /// principal on accept exactly like a gift sender; the SELLER receives it.
    /// This class runs AFTER the money moved, host-orchestrated:
    ///
    ///   HostExecuteTransfer  — re-key the rental ledger to the buyer, snapshot
    ///                          the seller's staff roster, send Finalize (buyer)
    ///                          + Release (seller); re-send Finalize until acked
    ///                          (recurrence-covered — a crash mid-claim resumes).
    ///   BuyerApplyFinalize   — the native takeover (OvertakeBusiness: furniture,
    ///                          licensing, demand bookkeeping) with schedule
    ///                          shifts preserved and staff PROMOTED from injected
    ///                          mirrors to real records (user ruling: workers
    ///                          transfer).  GenerateEmployees + the BizMan auto-
    ///                          open are suppressed for the duration.
    ///   SellerApplyRelease   — drop tenancy + real staff records (they live in
    ///                          the buyer's save now); the shop becomes a partner
    ///                          replica like any other.
    /// </summary>
    public static class MPOffers
    {
        /// <summary>True while BuyerApplyFinalize drives the native takeover —
        /// gates the GenerateEmployees skip and the BizMan auto-open skip.</summary>
        public static bool TransferInProgress { get; private set; }

        // ── HOST: orchestration ───────────────────────────────────────────────
        // OfferId → (payload, next send time). Re-sent every 5s until the buyer
        // acks; survives a buyer crash-rejoin within the session (the send tick
        // just keeps trying — SendHubTo to an absent player is a no-op).
        private static readonly Dictionary<string, (BizTransferPayload p, float nextAt)> _pendingFinalize = new();

        public static void Reset()
        {
            _pendingFinalize.Clear();
            _claimed.Clear();
            TransferInProgress = false;
        }

        /// <summary>HOST, main thread, called by MPHub.HostHandleAnswer AFTER the
        /// money moved for an accepted Kind="business" offer.</summary>
        public static void HostExecuteTransfer(LoanOfferPayload offer)
        {
            try
            {
                string addr = offer.AddressKey ?? "";
                string buyer = offer.From, seller = offer.To;
                if (string.IsNullOrEmpty(addr) || string.IsNullOrEmpty(buyer) || string.IsNullOrEmpty(seller)) return;

                // Ledger re-key: tenancy moves to the buyer ("host" if the buyer IS the host).
                MPServer.BuildingOwners[addr] = buyer == MPConfig.PlayerId ? "host" : buyer;

                var p = new BizTransferPayload
                {
                    OfferId = offer.Id, AddressKey = addr, BusinessName = offer.BusinessName ?? "",
                    BuyerId = buyer, SellerId = seller, Amount = offer.Principal,
                    Staff = MPRegisterSync.RosterCopyFor(addr, seller),
                    Shifts = CollectShifts(addr),
                };
                try { p.ItemCount = GameStatePatcher.FindRegistration(addr)?.itemInstances?.Count ?? 0; } catch { }

                // The buyer gets the shop's FULL interior with the sale. A client-buyer
                // holds nothing for a shop it never visited (host→client interiors are
                // on-demand; only client→host pushes are eager) — rig 2026-07-30: the
                // bought shop was an empty shell. The host's copy is authoritative for
                // every seller (its own save, or the seller-client's eager pushes).
                if (buyer != MPConfig.PlayerId)
                    try { InteriorSync.SendSnapshotToPlayer(addr, buyer, forceItemAuthority: true); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Offers] sale interior send: {ex.Message}"); }
                Plugin.Logger.LogInfo($"[Offers] transfer LEDGERED: '{p.BusinessName}' at {addr} — {seller} → {buyer} for ${p.Amount:N0} ({p.Staff.Count} staff ride along).");

                // Seller releases first (their machine is the interior/staff authority
                // until the buyer claims; release is safe — the buyer's claim needs
                // nothing further from them, the roster snapshot above is ours).
                if (seller == MPConfig.PlayerId) SellerApplyRelease(p);
                else MPServer.SendHubTo(seller, MessageType.BizTransferRelease, p);

                // Host is not the buyer: reflect the new tenancy in the host's own
                // world exactly like a confirmed rent (off the market, owner stamped).
                if (buyer != MPConfig.PlayerId)
                    GameStatePatcher.HostReflectPlayerRent(addr, buyer);

                // ALWAYS through the pending table — remote buyers get the 5s re-send,
                // and a host-as-buyer claim that throws retries the same way (rig
                // 2026-07-30: the direct call had no recurrence — one native NRE lost
                // the schedule restoration with no retry).
                _pendingFinalize[offer.Id] = (p, 0f);   // tick dispatches immediately
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Offers] HostExecuteTransfer: {ex.Message}"); }
        }

        /// <summary>HOST tick (main thread, cheap): re-send unacked Finalizes.</summary>
        public static void HostTick()
        {
            if (!MPServer.IsRunning || _pendingFinalize.Count == 0) return;
            try
            {
                List<string>? due = null;
                foreach (var kv in _pendingFinalize)
                    if (Time.unscaledTime >= kv.Value.nextAt) (due ??= new List<string>()).Add(kv.Key);
                if (due == null) return;
                foreach (var id in due)
                {
                    var (p, _) = _pendingFinalize[id];
                    _pendingFinalize[id] = (p, Time.unscaledTime + 5f);
                    if (p.BuyerId == MPConfig.PlayerId) BuyerApplyFinalize(p);   // host bought it — local claim, same recurrence
                    else
                    {
                        // Round-197: the sale's interior snapshot is Interior-family traffic —
                        // a buyer still inside its join-quiesce window DROPS it, and the claim
                        // then defers forever on an empty shop. Re-send it with every finalize
                        // beat until acked; the apply is idempotent.
                        try { InteriorSync.SendSnapshotToPlayer(p.AddressKey, p.BuyerId, forceItemAuthority: true); } catch { }
                        MPServer.SendHubTo(p.BuyerId, MessageType.BizTransferFinalize, p);
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Offers] HostTick: {ex.Message}"); }
        }

        public static void HostHandleAck(BizTransferPayload? p, string senderPid)
        {
            if (p == null) return;
            if (_pendingFinalize.TryGetValue(p.OfferId, out var have) && have.p.BuyerId == senderPid)
            {
                _pendingFinalize.Remove(p.OfferId);
                Plugin.Logger.LogInfo($"[Offers] transfer COMPLETE: '{have.p.BusinessName}' at {have.p.AddressKey} claimed by {senderPid}.");
            }
        }

        /// <summary>HOST: the sold shop's work schedule from the host's registration copy
        /// (the host either owns the shop or holds the synced schedule). Synthetic duty
        /// stand-ins are the seller's register-duty artifacts — they never travel.</summary>
        private static List<ShiftInfo> CollectShifts(string addr)
        {
            var list = new List<ShiftInfo>();
            try
            {
                var reg = GameStatePatcher.FindRegistration(addr);
                if (reg?.scheduleDays == null) return list;
                foreach (var d in reg.scheduleDays)
                {
                    if (d?.workShifts == null) continue;
                    foreach (var w in d.workShifts)
                    {
                        if (w == null || string.IsNullOrEmpty(w.employeeId)) continue;
                        if (w.employeeId.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)) continue;
                        var si = new ShiftInfo
                        {
                            Day = (int)d.day, EmployeeId = w.employeeId,
                            ItemInstanceId = w.itemInstanceId ?? "",
                            StartingHour = w.startingHour, EndingHour = w.endingHour,
                            Type = (int)w.type,
                        };
                        // Station identity — ids don't survive the machine boundary.
                        try
                        {
                            if (!string.IsNullOrEmpty(w.itemInstanceId)
                                && reg.itemInstances != null
                                && reg.itemInstances.TryGetValue(w.itemInstanceId, out var st) && st != null)
                            {
                                si.StationItemName = st.itemName ?? "";
                                si.StationX = st.position.x; si.StationY = st.position.y; si.StationZ = st.position.z;
                            }
                        }
                        catch { }
                        list.Add(si);
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Offers] collect shifts '{addr}': {ex.Message}"); }
            return list;
        }

        // ── BUYER: claim locally ──────────────────────────────────────────────
        private static readonly HashSet<string> _claimed = new();   // OfferIds already applied (re-sent Finalizes are idempotent)

        public static void BuyerApplyFinalize(BizTransferPayload? p)
        {
            if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
            try
            {
                if (_claimed.Contains(p.OfferId)) { Ack(p); return; }
                var reg = GameStatePatcher.FindRegistration(p.AddressKey);
                if (reg == null)
                {
                    // Building not materialized yet (fresh join mid-transfer) — the host's
                    // 5s re-send IS the retry; just don't ack.
                    Plugin.Logger.LogWarning($"[Offers] Finalize for '{p.AddressKey}' — registration not found yet; will retry on the next re-send.");
                    return;
                }
                int localItems = 0; try { localItems = reg.itemInstances?.Count ?? 0; } catch { }
                if (p.ItemCount > 0 && localItems == 0)
                {
                    // The sale's interior snapshot hasn't landed yet — claiming now would
                    // take an EMPTY shell (and the schedule would have no stations to bind
                    // to). The host's 5s re-send retries until the furniture is here.
                    Plugin.Logger.LogWarning($"[Offers] Finalize for '{p.AddressKey}' deferred — interior not materialized yet (0/{p.ItemCount} items); waiting for the sale's snapshot.");
                    return;
                }

                // The native takeover clones scheduleDays but DROPS the work shifts —
                // capture them (they reference the real staff ids we're promoting).
                var oldDays = reg.scheduleDays;

                MPRegisterSync.PromoteInjectedForTransfer(p.AddressKey);

                TransferInProgress = true;
                MPPatches.AuthorizedPlayerBusinessTransfer = p.AddressKey;
                try { BizManPresentation.OvertakeBusiness(reg); }
                catch (Exception ex)
                {
                    // The native tail past AddToPlayer is bookkeeping (POI, filters, demand) —
                    // never let it abort OUR restoration (rig 2026-07-30: an NRE here cost the
                    // transferred schedule). The tenancy check below decides real failure.
                    Plugin.Logger.LogWarning($"[Offers] native takeover threw mid-tail (continuing with restoration): {ex.Message}");
                }
                finally { TransferInProgress = false; MPPatches.AuthorizedPlayerBusinessTransfer = null; }

                if (!reg.RentedByPlayer)
                {
                    // The claim itself didn't land — leave unclaimed; the host's 5s re-send retries.
                    Plugin.Logger.LogWarning($"[Offers] claim at '{p.AddressKey}' did not take (still not rented) — will retry.");
                    return;
                }

                // Schedule restoration. PRIMARY: the wire copy (the buyer's local mirror
                // does not reliably carry the seller's shifts — rig 2026-07-30, workers
                // arrived unscheduled). FALLBACK: the pre-takeover local capture.
                // Synthetic duty stand-in shifts never carry (their records don't exist
                // here; they only spam 'Employee with ID BAMP_DUTY_… not found').
                try
                {
                    int applied = 0;
                    if (p.Shifts != null && p.Shifts.Count > 0 && reg.scheduleDays != null)
                    {
                        // Station re-binding: the wire ids are the SELLER's — interior item ids
                        // are per-machine. Resolve each distinct wire station to OUR item of the
                        // same name nearest its position; a shift bound to a station id this
                        // machine doesn't have renders as an empty schedule (rig 2026-07-30).
                        var stationMap = new Dictionary<string, string>();
                        int unresolved = 0;
                        foreach (var s in p.Shifts)
                        {
                            if (s == null || string.IsNullOrEmpty(s.ItemInstanceId) || stationMap.ContainsKey(s.ItemInstanceId)) continue;
                            string local = s.ItemInstanceId;   // default: trust the id (same-origin worlds)
                            try
                            {
                                bool haveLocally = reg.itemInstances != null && reg.itemInstances.ContainsKey(s.ItemInstanceId);
                                if (!haveLocally && !string.IsNullOrEmpty(s.StationItemName) && reg.itemInstances != null)
                                {
                                    float best = float.MaxValue; string? bestId = null;
                                    var target = new Vector3(s.StationX, s.StationY, s.StationZ);
                                    foreach (var kv in reg.itemInstances)
                                    {
                                        var ii = kv.Value;
                                        if (ii == null || ii.itemName != s.StationItemName) continue;
                                        float dd = (ii.position - target).sqrMagnitude;
                                        if (dd < best) { best = dd; bestId = kv.Key; }
                                    }
                                    if (bestId != null) local = bestId;
                                    else unresolved++;
                                }
                            }
                            catch { }
                            stationMap[s.ItemInstanceId] = local;
                        }
                        foreach (var nd in reg.scheduleDays)
                        {
                            if (nd == null) continue;
                            var shifts = new List<WorkShift>();
                            foreach (var s in p.Shifts)
                                if (s != null && s.Day == (int)nd.day && !string.IsNullOrEmpty(s.EmployeeId))
                                    shifts.Add(new WorkShift
                                    {
                                        employeeId = s.EmployeeId,
                                        itemInstanceId = !string.IsNullOrEmpty(s.ItemInstanceId) && stationMap.TryGetValue(s.ItemInstanceId, out var mapped) ? mapped : s.ItemInstanceId,
                                        startingHour = s.StartingHour, endingHour = s.EndingHour,
                                        type = (WorkShiftType)s.Type,
                                    });
                            if (shifts.Count > 0) { nd.workShifts = shifts; applied += shifts.Count; }
                        }
                        if (applied > 0) Plugin.Logger.LogInfo($"[Offers] schedule restored from wire: {applied} shift(s), {stationMap.Count} station(s) re-bound{(unresolved > 0 ? $", {unresolved} UNRESOLVED (no same-name item)" : "")}.");
                    }
                    if (applied == 0 && oldDays != null && reg.scheduleDays != null)
                        foreach (var nd in reg.scheduleDays)
                        {
                            var od = oldDays.Find(d => d != null && d.day == nd.day);
                            if (od?.workShifts == null || od.workShifts.Count == 0) continue;
                            var keep = od.workShifts.FindAll(w =>
                                w != null && !string.IsNullOrEmpty(w.employeeId)
                                          && !w.employeeId.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal));
                            if (keep.Count > 0) nd.workShifts = keep;
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Offers] schedule restore: {ex.Message}"); }

                // Staff not already present (mirror promotion covers the usual case;
                // this covers a buyer who never saw the roster, e.g. fresh joiner).
                int created = MPRegisterSync.EnsureRealStaff(p.AddressKey, p.Staff);

                _claimed.Add(p.OfferId);
                Plugin.Logger.LogInfo($"[Offers] CLAIMED '{p.BusinessName}' at {p.AddressKey} from {p.SellerId} (${p.Amount:N0}; staff promoted, +{created} created).");
                try { PassengerHud.Toast($"'{p.BusinessName}' is yours — {p.SellerId} accepted your ${p.Amount:N0} offer.", 6f); } catch { }
                Ack(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Offers] BuyerApplyFinalize: {ex.Message}"); }
        }

        private static void Ack(BizTransferPayload p)
        {
            try
            {
                var ack = new BizTransferPayload { OfferId = p.OfferId, AddressKey = p.AddressKey, BuyerId = MPConfig.PlayerId };
                if (MPServer.IsRunning) HostHandleAck(ack, MPConfig.PlayerId);
                else MPClient.SendHub(MessageType.BizTransferAck, ack);
            }
            catch { }
        }

        // ── SELLER: release locally ───────────────────────────────────────────
        public static void SellerApplyRelease(BizTransferPayload? p)
        {
            if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
            try
            {
                var reg = GameStatePatcher.FindRegistration(p.AddressKey);
                int dropped = MPRegisterSync.DropRealStaffAt(p.AddressKey);
                if (reg != null)
                {
                    reg.RentedByPlayer = false;
                    reg.AvailableForRent = false;   // not on the market — the buyer runs it
                    try { reg.businessOwnerRivalId = p.BuyerId; } catch { }
                }
                Plugin.Logger.LogInfo($"[Offers] RELEASED '{p.BusinessName}' at {p.AddressKey} to {p.BuyerId} (${p.Amount:N0} received; {dropped} staff record(s) transferred out).");
                try { PassengerHud.Toast($"Sold '{p.BusinessName}' to {p.BuyerId} for ${p.Amount:N0}.", 6f); } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Offers] SellerApplyRelease: {ex.Message}"); }
        }
    }
}
