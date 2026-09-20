using System;
using System.Collections.Generic;
using BigAmbitions.Items;                 // CargoInstance, ItemAmountTarget, ItemsGetter
using BigAmbitions.Tags;                  // TagRef
using Buildings;                          // BuildingRegistration
using Entities;                           // ImportPartnership, ImportProduct, Warehouse, DeliveryTransaction, Contact, TextMessage, Address, DeliveryHelper
using Extensions;                         // ToCurrencyFormat
using HarmonyLib;                         // AccessTools - the game's OWN private statics/methods are driven, never re-written
using Helpers;                            // BuildingHelper, EmployeeHelper, ItemHelper, ProductMarketHelper, LocalizationHelper, TimeHelper
using Localizor;                          // string.GetLocalization()
using UI.Smartphone.Apps.Contacts;        // ContactCategoryName

namespace BigAmbitionsMP
{
    /// <summary>
    /// H-MERGERIMPORT-1 (batch 24, user-approved 2026-09-21): A MEMBER'S HQ PURCHASING AGENT DELIVERS TO A
    /// MERGED PARTNER'S WAREHOUSE.
    ///
    /// THE PROBLEM. Entities.ImportPartnership.DoDeliveries prices (:184) and delivers (:272) only products
    /// whose assigned warehouse answers RentedByPlayer. That method is in the authority veil's Steps table
    /// (MPPatches.Patch_MergerAuthorityVeil), so for the whole pass every merger-FLIPPED partner building
    /// reverts to native truth - RentedByPlayer false - and a line aimed at a partner's warehouse is skipped
    /// in silence: no charge, no delivery, no message. Un-veiling is not an option (the veil is what stops
    /// both machines charging for the same import), and a replica cannot count the partner's pallets or
    /// place goods on the partner's shelves.
    ///
    /// THE SHAPE, copied from CargoTransfer (4c part 2): each native effect lands ONCE, on the machine that
    /// physically holds the object, joined by four legs of one wire type (MessageType.ImportTransfer) with
    /// the HOST as the authority in the middle.
    ///
    ///   I1 DETACH    a Prefix on DoDeliveries lifts every partner-warehouse ImportProduct out of
    ///                plan.products before native runs, and a Finalizer puts them back at their original
    ///                indices even when native throws. Native therefore runs exactly once, on its own
    ///                lines only, and books one charge for them - the routed lines are a SECOND bank line
    ///                on a mixed plan (user ruling 2026-09-21: two lines are acceptable).
    ///   I2 NEED      plan owner -> host -> the warehouse's runner: "how much of each item do you still
    ///                need?" - ImportProduct.GetAmountToBuy's own arithmetic (:39-51), run where the
    ///                pallets really are. A refusal charges NOTHING.
    ///   I3 CHARGE    the plan owner applies the weekly-cap arithmetic (:188-211) against its OWN ledgers,
    ///                pays ONE charge with the game's own TransactionInfo and tax flag (:236-247) - or
    ///                raises the game's own not-enough-funds message and re-arms exactly as :249-262 - and
    ///                only then sends the deliver leg.
    ///   I4 DELIVER   the warehouse's runner runs the game's own ItemHelper.DeliverCargoToBuilding with the
    ///                native iswarehousestorage filter (:278), books the positive DeliveryTransaction
    ///                (:282-287) and acks the per-item REMAINDER. Idempotent by TransferId, PERSISTED.
    ///   I5 ACK       the plan owner books the delivered half (:288-305) and pays the game's own refund
    ///                (:306-337) for the remainder, then answers "closed" so the host drops its record.
    ///
    /// NO NEW ON-SCREEN TEXT: the only messages raised are the game's own
    /// ba:messagetype_phone_import_partnership_delivery_not_enough_funds and
    /// ..._no_available_space (and, in the one branch where the detach made native skip its own agent
    /// check, ..._employee_not_found), on the game's own importer Contact.
    ///
    /// MONEY. A member's GameManager.ChangeMoneySafe already forwards to the shared wallet, veil or not
    /// (MergerWallet.cs:56-79), so the charge and the refund need no merger-specific handling at all.
    ///
    /// THREADING. Everything here touches game state and runs on the Unity main thread: the detach is inside
    /// the native pass, and every inbound leg is marshalled by the network dispatch (MPServer/MPClient)
    /// exactly like CargoTransfer.
    /// </summary>
    internal static class ImportTransfer
    {
        internal const string ActNeed = "need", ActDeliver = "deliver", ActAck = "ack", ActClosed = "closed";

        private const string MsgNoFunds  = "ba:messagetype_phone_import_partnership_delivery_not_enough_funds";
        private const string MsgNoSpace  = "ba:messagetype_phone_import_partnership_delivery_no_available_space";
        private const string MsgNoAgent  = "ba:messagetype_phone_import_partnership_delivery_employee_not_found";
        private const string TxDelivery  = "ba:transaction_importdelivery";
        private const string TxRefund    = "ba:transaction_importdeliveryrefund";

        // -- PLAN-OWNER SIDE --

        /// <summary>One routed line this machine has PAID for and not yet closed. Persisted: the money has
        /// left the wallet, so a restart must not lose the row that books the goods or refunds them.</summary>
        internal sealed class Pending
        {
            public string TransferId = "", PlanId = "", ImporterKey = "", DestKey = "";
            public int    Day, Hour;
            public bool   WasUrgent, IsTarget;
            public float  Charged;
            public List<ImportTransferItem> Items = new();
        }

        private static readonly Dictionary<string, Pending> _pending = new();                 // persisted
        private static readonly HashSet<string> _started = new();                             // one need leg per id
        private static readonly Dictionary<string, MarkTime> _closed = new();                 // persisted: acks already applied

        /// <summary>M4 (fold 2): the GAME (day, hour) one persisted mark was minted at. Every row type has to
        /// carry it, because the rewind check below compares it with the clock of the save that was actually
        /// loaded.</summary>
        private readonly struct MarkTime
        {
            public readonly int Day, Hour;
            public MarkTime(int day, int hour) { Day = day; Hour = hour; }
            public bool LaterThan(int day, int hour) => Day > day || (Day == day && Hour > hour);
        }

        // -- DESTINATION SIDE --

        /// <summary>Ids this machine has already DELIVERED, with the ack it sent. A repeated "deliver" for
        /// one of these re-acks with the identical figures and places nothing. Persisted.</summary>
        private static readonly Dictionary<string, ImportTransferPayload> _applied = new();

        private static string _lastLine = "";

        private static int DayNow()  { try { return SaveGameManager.Current?.Day  ?? 0; } catch { return 0; } }
        private static int HourNow() { try { return SaveGameManager.Current?.Hour ?? 0; } catch { return 0; } }

        private static void Trim<T>(Dictionary<string, T> d) { if (d.Count > 2000) d.Clear(); }
        private static void Trim(HashSet<string> s)          { if (s.Count > 2000) s.Clear(); }

        internal static int PendingCount => _pending.Count;
        internal static int AppliedCount => _applied.Count;
        internal static int ClosedCount  => _closed.Count;
        internal static string LastLine   => _lastLine;

        private static void Note(string line) { _lastLine = line; Plugin.Logger.LogInfo("[Import] " + line); }

        private static void Send(ImportTransferPayload p)
        {
            try
            {
                p.PlayerId = MPConfig.PlayerId;
                if (MPServer.IsRunning) MPServer.HostRouteImportTransfer(p, MPConfig.PlayerId);
                else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.ImportTransfer, MPConfig.PlayerId, p));
                else Plugin.Logger.LogWarning($"[Import] transfer {p.TransferId} refused: this machine is not connected to a session.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] transfer {p.TransferId}: the leg could not be sent - {ex.GetType().Name}: {ex.Message}"); }
        }

        // ══════════ I1  THE DETACH (Prefix + Finalizer on the native pass) ══════════

        /// <summary>What the Prefix lifted out, carried to the Finalizer through Harmony's __state.</summary>
        internal sealed class DetachState
        {
            public bool Armed;
            public bool WasUrgent;
            public bool EmptiedTheList;                      // native returned at :163 and never ran its agent check
            public readonly List<int> Index = new();
            public readonly List<ImportProduct> Product = new();
        }

        /// <summary>MAIN THREAD, inside the native pass. TRUE when this plan's products were touched.</summary>
        internal static void Detach(ImportPartnership plan, DetachState st)
        {
            try
            {
                if (plan == null || st == null) return;
                if (!MergerSync.IAmMember) return;
                if (plan.products == null || plan.products.Count == 0) return;

                // The native entry test, replicated WITHOUT its side effects (:151-155). EnsureDeliveryDayIsNotInPast
                // is pure but logs a Unity error when it moves a past day, so its arithmetic is inlined instead of
                // called twice.
                int nd = plan.nextDeliveryDay;
                if (plan.isActive && nd < TimeHelper.CurrentDay) nd = TimeHelper.CurrentDay + 1;
                if (!plan.isActive || nd != TimeHelper.CurrentDay) return;

                st.WasUrgent = plan.isUrgentOrder;            // captured BEFORE native clears it at :162

                for (int i = plan.products.Count - 1; i >= 0; i--)
                {
                    var product = plan.products[i];
                    if (product == null || product.assignedWarehouse == null) continue;
                    BuildingRegistration? reg = null;
                    try { reg = BuildingHelper.GetBuildingRegistration(product.assignedWarehouse); } catch { }
                    if (reg == null) continue;
                    string key = ""; try { key = GameStateReader.AddressKey(reg); } catch { }
                    if (key.Length == 0) continue;
                    // The flip TABLE is read, never the flag: the veil's own Prefix may already have reverted
                    // RentedByPlayer on this reg, and patch order between the two must not decide anything.
                    // BooksHere is true on the ONE machine standing in for an absent owner - there the reg stays
                    // flipped through the veil and native delivers locally, exactly as it does today.
                    if (!MergerFlip.IsFlipped(key) || MergerFlip.BooksHere(reg)) continue;
                    st.Index.Add(i); st.Product.Add(product);
                    plan.products.RemoveAt(i);
                }
                if (st.Product.Count == 0) return;
                st.Armed = true;
                st.EmptiedTheList = plan.products.Count == 0;
            }
            // L7: a throw here leaves st.Armed false, so the Finalizer re-attaches everything that was already
            // lifted and starts NO legs - this plan's partner-warehouse lines are simply skipped for the day,
            // with this warning as their only trace. Native still runs, on its own lines.
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] the partner-warehouse detach failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>MAIN THREAD, the Finalizer. The re-attach is UNCONDITIONAL - a native throw must never
        /// leave a plan with half its products.</summary>
        internal static void Reattach(ImportPartnership plan, DetachState st)
        {
            if (plan == null || st == null || st.Product.Count == 0) return;
            try
            {
                // The indices were collected walking BACKWARDS, so replaying them in reverse re-inserts each
                // product at the index it had.
                for (int i = st.Product.Count - 1; i >= 0; i--)
                {
                    int at = st.Index[i];
                    if (plan.products == null) break;
                    if (at < 0 || at > plan.products.Count) at = plan.products.Count;
                    plan.products.Insert(at, st.Product[i]);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] the partner-warehouse re-attach failed: {ex.GetType().Name}: {ex.Message}"); }
            try { if (st.Armed) Begin(plan, st); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] the routed import legs could not be started: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ══════════ I2  THE NEED ASK (plan owner) ══════════

        private static void Begin(ImportPartnership plan, DetachState st)
        {
            string planId = plan.id ?? "";
            if (planId.Length == 0) { Plugin.Logger.LogWarning("[Import] a plan with no id cannot be routed - its partner-warehouse lines are skipped."); return; }

            var agent = plan.PurchasingAgentInstance;
            if (agent == null || agent.isBeingReplaced)
            {
                // Native normally messages and disarms here (:170-178). When the detach emptied the product
                // list native returned at :163 FIRST and never reached that check, so the game's own effect is
                // reproduced here - and only there, so nothing is ever said twice.
                if (st.EmptiedTheList)
                {
                    try
                    {
                        var importReg = BuildingHelper.GetBuildingRegistration(plan.importAddress);
                        Contact.GetContact(importReg, ContactCategoryName.ImportsAndGoods)?.SendMessage(new TextMessage(MsgNoAgent));
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] the missing-agent message could not be raised: {ex.GetType().Name}: {ex.Message}"); }
                    if (agent == null) plan.isActive = false;
                }
                Plugin.Logger.LogInfo($"[Import] plan '{planId}': no purchasing agent - {st.Product.Count} partner-warehouse line(s) are not routed.");
                return;
            }

            string importerKey = ""; try { importerKey = GameStateReader.AddressKey(plan.importAddress); } catch { }
            int day = DayNow(), hour = HourNow();

            // ONE leg per destination key, each line of that warehouse on it.
            var order = new List<string>();
            var byDest = new Dictionary<string, List<ImportProduct>>();
            foreach (var product in st.Product)
            {
                if (product == null || product.assignedWarehouse == null) continue;
                if (product.amount <= 0) continue;
                bool backorder = false;
                try { backorder = ProductMarketHelper.IsProductInMarketEvent(product.itemName, MarketEventType.ProductBackorder, string.Empty); } catch { }
                if (backorder) continue;                              // :184's third guard
                string key = ""; try { key = GameStateReader.AddressKey(BuildingHelper.GetBuildingRegistration(product.assignedWarehouse)); } catch { }
                if (key.Length == 0) continue;
                if (!byDest.TryGetValue(key, out var list)) { list = new List<ImportProduct>(); byDest[key] = list; order.Add(key); }
                list.Add(product);
            }

            foreach (var destKey in order)
            {
                string tid = planId + "|" + destKey + "|" + day + "|" + hour;
                if (_pending.ContainsKey(tid) || _started.Contains(tid) || _closed.ContainsKey(tid)) continue;

                var ask = new ImportTransferPayload
                {
                    Action = ActNeed, TransferId = tid, PlanId = planId, ImporterKey = importerKey,
                    DestKey = destKey, Day = day, Hour = hour, IsTarget = plan.isTarget,
                };
                var seen = new Dictionary<string, ImportTransferItem>();
                foreach (var product in byDest[destKey])
                {
                    string name = product.itemName ?? "";
                    if (name.Length == 0) continue;
                    if (seen.TryGetValue(name, out var row) && row != null) { row.Amount += product.amount; continue; }
                    row = new ImportTransferItem { ItemName = name, Amount = product.amount };
                    seen[name] = row; ask.Items.Add(row);
                }
                if (ask.Items.Count == 0) continue;

                _started.Add(tid); Trim(_started);
                Note($"transfer {tid} started - '{importerKey}' -> '{destKey}' ({ask.Items.Count} item(s), target={plan.isTarget}, urgent={st.WasUrgent}).");
                _urgentHint[tid] = st.WasUrgent; Trim(_urgentHint);
                Send(ask);
            }
        }

        /// <summary>The urgent flag is captured in the Prefix and needed again when the answer comes back a
        /// moment later; it never has to survive a restart, because an unanswered ask has cost nothing.</summary>
        private static readonly Dictionary<string, bool> _urgentHint = new();

        // ══════════ I2b  THE NEED ANSWER (the warehouse's runner) ══════════

        private static void AnswerNeed(ImportTransferPayload p)
        {
            var reply = new ImportTransferPayload
            {
                Action = ActNeed, Answer = true, TransferId = p.TransferId, PlanId = p.PlanId,
                ImporterKey = p.ImporterKey, DestKey = p.DestKey, Day = p.Day, Hour = p.Hour,
                IsTarget = p.IsTarget, TargetPid = p.SourcePid,
            };
            try
            {
                var reg = GameStatePatcher.FindRegistration(p.DestKey ?? "");
                if (reg == null || !(reg is Warehouse))
                {
                    reply.Reason = "this machine does not hold that warehouse";
                    Plugin.Logger.LogWarning($"[Import] transfer {p.TransferId} refused: {reply.Reason} ('{p.DestKey}').");
                    Send(reply); return;
                }
                int want = 0;
                if (p.Items != null)
                    foreach (var it in p.Items)
                    {
                        if (it == null || it.Amount <= 0 || string.IsNullOrEmpty(it.ItemName)) continue;
                        int need = it.Amount;
                        if (p.IsTarget)
                        {
                            int have = 0;
                            try { have = BuildingHelper.CountResourcesInPallets(reg.Address, it.ItemName); } catch { }
                            need = it.Amount - have;                        // ImportProduct.GetAmountToBuy :45-50
                            if (need < 0) need = 0;
                        }
                        if (need <= 0) continue;
                        reply.Items.Add(new ImportTransferItem { ItemName = it.ItemName, Amount = need });
                        want += need;
                    }
                Note($"transfer {p.TransferId} need answered - {reply.Items.Count} item(s), {want} unit(s) wanted at '{p.DestKey}'.");
            }
            catch (Exception ex)
            {
                reply.Items.Clear();
                reply.Reason = $"{ex.GetType().Name}: {ex.Message}";
                Plugin.Logger.LogWarning($"[Import] transfer {p.TransferId} refused: the need could not be counted - {reply.Reason}");
            }
            Send(reply);
        }

        // ══════════ I3  THE CHARGE (plan owner) ══════════

        private static ImportPartnership? FindPlan(string planId)
        {
            try
            {
                var list = SaveGameManager.Current?.importPartnerships;
                if (list == null || string.IsNullOrEmpty(planId)) return null;
                foreach (var plan in list) if (plan != null && plan.id == planId) return plan;
            }
            catch { }
            return null;
        }

        /// <summary>ImportPartnership.GetMaxOrderAmountPerImporter (:344-352), which is private.</summary>
        private static int MaxOrderAmountPerImporter(string itemName)
        {
            int num = ItemsGetter.GetByName(itemName).maxOrderAmountPerImporter;
            if (ProductMarketHelper.IsProductInMarketEvent(itemName, MarketEventType.ProductShortage))
                num = UnityEngine.Mathf.RoundToInt(num * 0.66f);
            return num;
        }

        private static void OnNeedAnswer(ImportTransferPayload p)
        {
            string tid = p.TransferId ?? "";
            if (tid.Length == 0) return;
            if (_pending.ContainsKey(tid) || _closed.ContainsKey(tid)) return;        // already paid / already finished
            bool wasUrgent = _urgentHint.TryGetValue(tid, out var u) && u;

            if (!string.IsNullOrEmpty(p.Reason))
            { Plugin.Logger.LogWarning($"[Import] transfer {tid} refused: {p.Reason} - nothing is charged."); _urgentHint.Remove(tid); return; }
            if (p.Items == null || p.Items.Count == 0)
            { Note($"transfer {tid}: the warehouse needs nothing - nothing is charged."); _urgentHint.Remove(tid); return; }

            var plan = FindPlan(p.PlanId ?? "");
            if (plan == null)
            { Plugin.Logger.LogWarning($"[Import] transfer {tid} refused: this machine no longer holds plan '{p.PlanId}' - nothing is charged."); _urgentHint.Remove(tid); return; }

            BuildingRegistration? importReg = null;
            try { importReg = BuildingHelper.GetBuildingRegistration(plan.importAddress); } catch { }
            if (importReg == null)
            { Plugin.Logger.LogWarning($"[Import] transfer {tid} refused: the importer '{p.ImporterKey}' is not on this machine - nothing is charged."); _urgentHint.Remove(tid); return; }
            Contact? contact = null;
            try { contact = Contact.GetContact(importReg, ContactCategoryName.ImportsAndGoods); } catch { }

            float discount = 1f; try { discount = plan.GetDiscount; } catch { }
            float total = 0f;
            var buy = new List<ImportTransferItem>();
            var exceeded = new List<string>();
            var perItem = new Dictionary<string, int>();
            bool limitsOff = false; try { limitsOff = DeliveryHelper.AreWholesaleAndImportLimitsDisabled(); } catch { }

            foreach (var it in p.Items)
            {
                if (it == null || it.Amount <= 0 || string.IsNullOrEmpty(it.ItemName)) continue;
                var product = FindProduct(plan, it.ItemName, p.DestKey ?? "");
                if (product == null) continue;                      // the plan changed under the leg - buy nothing for it
                int n = it.Amount;
                int cap = int.MaxValue;
                try { if (!limitsOff && DeliveryHelper.ShouldLimitImporterMaxAmount(it.ItemName, plan.importAddress)) cap = MaxOrderAmountPerImporter(it.ItemName); }
                catch { }
                // F4: the weekly cap is measured against the PLAN OWNER's own weekly ledger only, never the
                // destination's - which is right, because the ledger the game caps on is the importer's, and the
                // importer is this machine's.
                int already = 0;
                try { already = ImportPartnership.GetItemAmountOrderedThisWeek(plan.importAddress, it.ItemName); } catch { }
                cap = UnityEngine.Mathf.Max(0, cap == int.MaxValue ? int.MaxValue : cap - already);
                if (n > cap) { exceeded.Add(it.ItemName); n = cap; }
                if (n <= 0) continue;
                float price = 0f; try { price = product.Price; } catch { }
                total += price * n * discount;
                perItem[it.ItemName] = (perItem.TryGetValue(it.ItemName, out var had) ? had : 0) + n;
                buy.Add(new ImportTransferItem { ItemName = it.ItemName, Amount = n, PricePerUnit = price * discount });
            }

            // (g) the game's own weekly-limit message and large-purchase events, for the ROUTED items only.
            RaiseNativeGroupNotices(plan.importAddress, exceeded, perItem);

            if (buy.Count == 0 || total <= 0f)
            { Note($"transfer {tid}: nothing left to buy after the weekly cap - nothing is charged."); _urgentHint.Remove(tid); return; }

            if (wasUrgent)
            { try { total *= DeliveryHelper.GetImporterUrgentFeeMultiplier(); } catch { } }

            var data = new Dictionary<string, string> { { "businessName", importReg.BusinessName } };
            bool taxDeductible = false;
            try { taxDeductible = importReg.BuildingCached.SpecialService?.hasTaxDeductiblePurchases ?? false; } catch { }
            var ti = new TransactionInfo(TxDelivery, data);
            if (taxDeductible) ti.SetTaxDeductibleName("tax_import");

            bool paid = false;
            try { paid = GameManager.ChangeMoneySafe(0f - total, ti); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] transfer {tid}: the charge threw - {ex.GetType().Name}: {ex.Message}"); }
            if (!paid)
            {
                // :249-262, whole: the game's own message and the game's own re-arm.
                try
                {
                    var md = new Dictionary<string, string> { { "amount", total.ToCurrencyFormat() } };
                    contact?.SendMessage(new TextMessage(MsgNoFunds, md));
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] transfer {tid}: the not-enough-funds message could not be raised - {ex.GetType().Name}: {ex.Message}"); }
                try
                {
                    if (!plan.isRepeatingOrder) plan.isActive = false;
                    else if (wasUrgent) { plan.isActive = true; plan.isUrgentOrder = true; plan.nextDeliveryDay = TimeHelper.CurrentDay + 1; }
                }
                catch { }
                Plugin.Logger.LogWarning($"[Import] transfer {tid} refused: not enough funds for {total:0.##} - nothing is delivered.");
                _urgentHint.Remove(tid);
                return;
            }

            var t = new Pending
            {
                TransferId = tid, PlanId = plan.id ?? "", ImporterKey = p.ImporterKey ?? "", DestKey = p.DestKey ?? "",
                Day = p.Day, Hour = p.Hour, WasUrgent = wasUrgent, IsTarget = p.IsTarget, Charged = total,
            };
            foreach (var b in buy) t.Items.Add(new ImportTransferItem { ItemName = b.ItemName, Amount = b.Amount, PricePerUnit = b.PricePerUnit });
            _pending[tid] = t; _writtenThisWorld.Add(tid); Trim(_pending); _urgentHint.Remove(tid);
            PersistMarks();
            Note($"transfer {tid} charged ${total:0.##}");

            var leg = new ImportTransferPayload
            {
                Action = ActDeliver, TransferId = tid, PlanId = t.PlanId, ImporterKey = t.ImporterKey,
                DestKey = t.DestKey, Day = t.Day, Hour = t.Hour, IsTarget = t.IsTarget,
            };
            foreach (var b in t.Items) leg.Items.Add(new ImportTransferItem { ItemName = b.ItemName, Amount = b.Amount, PricePerUnit = b.PricePerUnit });
            Send(leg);
        }

        private static ImportProduct? FindProduct(ImportPartnership plan, string itemName, string destKey)
        {
            try
            {
                if (plan?.products == null) return null;
                foreach (var product in plan.products)
                {
                    if (product == null || product.itemName != itemName || product.assignedWarehouse == null) continue;
                    string key = ""; try { key = GameStateReader.AddressKey(BuildingHelper.GetBuildingRegistration(product.assignedWarehouse)); } catch { }
                    if (key == destKey) return product;
                }
            }
            catch { }
            return null;
        }

        /// <summary>(g) The game's OWN group notices - SendAmountExceededNotification (:354-364) and
        /// CreateLargePurchaseEvents (:366-375), both private statics driven off two private static tables that
        /// DoAllDeliveries clears per importer group (:84) and consumes immediately (:99-100). A routed answer
        /// is not expected to land inside that pass - it arrives through the network dispatch's main-thread
        /// queue, and the one leg that is handled synchronously (this machine is the destination's runner) is
        /// unreachable today, because a warehouse this machine runs is never detached in the first place. What
        /// actually MAKES this safe is the save-and-restore below: both tables are put back in the finally, so
        /// even a call that did land inside a native group leaves that group's figures as it found them.</summary>
        private static void RaiseNativeGroupNotices(Address importAddress, List<string> exceeded, Dictionary<string, int> perItem)
        {
            if (importAddress == null) return;
            if ((exceeded == null || exceeded.Count == 0) && (perItem == null || perItem.Count == 0)) return;
            try
            {
                var fEx  = AccessTools.Field(typeof(ImportPartnership), "AmountExceededItems");
                var fAmt = AccessTools.Field(typeof(ImportPartnership), "ImporterAmountPerItem");
                var mEx  = AccessTools.Method(typeof(ImportPartnership), "SendAmountExceededNotification");
                var mBig = AccessTools.Method(typeof(ImportPartnership), "CreateLargePurchaseEvents");
                if (fEx == null || fAmt == null || mEx == null || mBig == null)
                {
                    Plugin.Logger.LogWarning("[Import] the game's own weekly-limit / large-purchase statics were not found - those two notices are skipped for the routed lines.");
                    return;
                }
                var oldEx  = fEx.GetValue(null)  as HashSet<string>;
                var oldAmt = fAmt.GetValue(null) as Dictionary<string, int>;
                var keepEx  = oldEx  != null ? new HashSet<string>(oldEx)            : new HashSet<string>();
                var keepAmt = oldAmt != null ? new Dictionary<string, int>(oldAmt)   : new Dictionary<string, int>();
                try
                {
                    if (oldEx != null)  { oldEx.Clear();  if (exceeded != null) foreach (var n in exceeded) oldEx.Add(n); }
                    if (oldAmt != null) { oldAmt.Clear(); if (perItem != null) foreach (var kv in perItem) oldAmt[kv.Key] = kv.Value; }
                    if (exceeded != null && exceeded.Count > 0) mEx.Invoke(null, new object[] { importAddress });
                    if (perItem  != null && perItem.Count  > 0) mBig.Invoke(null, new object[] { importAddress });
                }
                finally
                {
                    if (oldEx != null)  { oldEx.Clear();  foreach (var n in keepEx) oldEx.Add(n); }
                    if (oldAmt != null) { oldAmt.Clear(); foreach (var kv in keepAmt) oldAmt[kv.Key] = kv.Value; }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] the routed weekly-limit / large-purchase notices failed - {ex.GetType().Name}: {ex.Message}"); }
        }

        // ══════════ I4  THE DELIVERY (the warehouse's runner) ══════════

        private static void ApplyDeliver(ImportTransferPayload p)
        {
            string tid = p.TransferId ?? "";
            if (_applied.TryGetValue(tid, out var already) && already != null)
            {
                Note($"transfer {tid} delivered already - acknowledged again, nothing placed.");
                Send(already); return;
            }
            var ack = new ImportTransferPayload
            {
                Action = ActAck, TransferId = tid, PlanId = p.PlanId, ImporterKey = p.ImporterKey,
                DestKey = p.DestKey, Day = p.Day, Hour = p.Hour, IsTarget = p.IsTarget, TargetPid = p.SourcePid,
            };
            // M2 (fold 2): the POSITION in p.Items of the first entry the loop below has not finished with.
            // The outer catch appends full remainders from here on only - everything before it already has the
            // row it really landed with, and some of those units are shelved and booked.
            int nextItem = 0;
            try
            {
                var warehouse = GameStatePatcher.FindRegistration(p.DestKey ?? "") as Warehouse;
                if (warehouse == null)
                {
                    ack.Reason = "this machine does not hold that warehouse";
                    if (p.Items != null)
                        foreach (var it in p.Items)
                            if (it != null) ack.Items.Add(new ImportTransferItem { ItemName = it.ItemName, Amount = it.Amount, PricePerUnit = it.PricePerUnit, Remainder = it.Amount });
                    Plugin.Logger.LogWarning($"[Import] transfer {tid} refused: {ack.Reason} ('{p.DestKey}') - every unit is refunded.");
                    MarkApplied(tid, ack); Send(ack); return;
                }

                int delivered = 0, remainder = 0;
                var rows = p.Items ?? new List<ImportTransferItem>();
                for (int i = 0; i < rows.Count; i++)
                {
                    nextItem = i;
                    var it = rows[i];
                    if (it == null || it.Amount <= 0 || string.IsNullOrEmpty(it.ItemName)) { nextItem = i + 1; continue; }
                    string itemName = it.ItemName;
                    // M2: ONE item's own try/catch. Whatever this item throws is that item's full remainder and
                    // nothing else's - the items already on the shelf keep the figures they landed with.
                    CargoInstance? cargo = null;          // fold 3: outside the try - the catch must see what was really left
                    try
                    {
                        cargo = new CargoInstance(itemName, it.Amount, it.PricePerUnit);
                        try { ItemHelper.DeliverCargoToBuilding(cargo, warehouse, (ItemInstance x) => x.ItemCached.HasTag(TagRef.Itemtag.iswarehousestorage)); }   // :278
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] transfer {tid}: placing '{itemName}': {ex.GetType().Name}: {ex.Message}"); }
                        int left = cargo.amount, took = it.Amount - left;
                        if (took > 0)
                        {
                            try
                            {
                                var tx = new DeliveryTransaction();                                  // :282-287
                                tx.SetItems(new List<DeliveryItem> { new DeliveryItem(itemName, took) });
                                warehouse.deliveryTransactions.Add(tx);
                            }
                            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] transfer {tid}: the delivery record: {ex.GetType().Name}: {ex.Message}"); }
                            delivered += took;
                        }
                        remainder += left;
                        ack.Items.Add(new ImportTransferItem { ItemName = itemName, Amount = it.Amount, PricePerUnit = it.PricePerUnit, Remainder = left });
                    }
                    catch (Exception ex)
                    {
                        // Fold 3 (re-check M2): refund what is REALLY left. If the throw came after the shelving call,
                        // cargo.amount is what did not fit; only a throw before it (cargo == null) is a full remainder.
                        int leftC = cargo != null ? Math.Max(0, Math.Min(it.Amount, cargo.amount)) : it.Amount;
                        Plugin.Logger.LogWarning($"[Import] transfer {tid}: '{itemName}' could not be delivered cleanly - {ex.GetType().Name}: {ex.Message} - {leftC} of {it.Amount} refunded for that item alone.");
                        nextItem = i + 1;                 // this position is accounted for below, never again by the outer catch
                        ack.Items.Add(new ImportTransferItem { ItemName = itemName, Amount = it.Amount, PricePerUnit = it.PricePerUnit, Remainder = leftC });
                        remainder += leftC; delivered += it.Amount - leftC;
                    }
                    nextItem = i + 1;
                }

                try { InstanceBehavior<GameManager>.Instance.shouldUpdateAfterDeliveries = true; } catch { }
                if (delivered > 0) { try { GameEvent.Invoke("ba:gameevent_itemcargochanged"); } catch { } }   // DoAllDeliveries :109
                Note($"transfer {tid} delivered {delivered} (remainder {remainder}) at '{p.DestKey}'.");
            }
            catch (Exception ex)
            {
                // M2 (fold 2): the rows ALREADY in ack.Items stand. Clearing them and calling every item a full
                // remainder refunds goods that are on a shelf; only the entries this pass never reached are
                // added, matched by POSITION, because the same item name may appear twice in one leg.
                var rest = p.Items ?? new List<ImportTransferItem>();
                for (int i = nextItem; i < rest.Count; i++)
                {
                    var it = rest[i];
                    if (it == null || it.Amount <= 0 || string.IsNullOrEmpty(it.ItemName)) continue;
                    ack.Items.Add(new ImportTransferItem { ItemName = it.ItemName, Amount = it.Amount, PricePerUnit = it.PricePerUnit, Remainder = it.Amount });
                }
                ack.Reason = $"{ex.GetType().Name}: {ex.Message}";
                Plugin.Logger.LogWarning($"[Import] transfer {tid}: the delivery stopped at item {nextItem} - {ack.Reason} - what was already shelved keeps its figures, the rest is refunded.");
            }
            MarkApplied(tid, ack);
            Send(ack);
        }

        // ══════════ I5  THE ACKNOWLEDGEMENT (plan owner) ══════════

        private static void OnAck(ImportTransferPayload p)
        {
            string tid = p.TransferId ?? "";
            if (tid.Length == 0) return;
            if (_closed.ContainsKey(tid)) { Note($"transfer {tid} closed already - confirmed again, nothing booked."); SendClosed(p); return; }
            if (!_pending.TryGetValue(tid, out var t) || t == null)
            {
                Plugin.Logger.LogWarning($"[Import] transfer {tid}: an acknowledgement for a row this machine does not hold - nothing is booked or refunded.");
                SendClosed(p); return;
            }

            var plan = FindPlan(t.PlanId);
            BuildingRegistration? importReg = null;
            try { if (plan != null) importReg = BuildingHelper.GetBuildingRegistration(plan.importAddress); } catch { }
            Contact? contact = null;
            try { if (importReg != null) contact = Contact.GetContact(importReg, ContactCategoryName.ImportsAndGoods); } catch { }
            var destReg = GameStatePatcher.FindRegistration(t.DestKey ?? "");
            string warehouseName = ""; try { warehouseName = destReg != null ? destReg.BusinessName : ""; } catch { }

            float urgent = 1f;
            if (t.WasUrgent) { try { urgent = DeliveryHelper.GetImporterUrgentFeeMultiplier(); } catch { urgent = 1f; } }

            int booked = 0, refundedUnits = 0; float refunded = 0f;
            try
            {
                foreach (var row in t.Items)
                {
                    if (row == null || string.IsNullOrEmpty(row.ItemName)) continue;
                    int left = 0;
                    if (p.Items != null)
                        foreach (var ai in p.Items)
                            if (ai != null && ai.ItemName == row.ItemName) { left = ai.Remainder; break; }
                    if (left < 0) left = 0;
                    if (left > row.Amount) left = row.Amount;
                    int got = row.Amount - left;

                    if (got > 0)
                    {
                        // :288-305, on the plan owner's own ledgers.
                        var product = plan != null ? FindProduct(plan, row.ItemName, t.DestKey ?? "") : null;
                        if (product != null) product.amountOrderedThisWeek += got;
                        try
                        {
                            var gi = SaveGameManager.Current;
                            if (gi != null && plan != null)
                            {
                                if (gi.itemsOrderedThisWeekByImporter == null) gi.itemsOrderedThisWeekByImporter = new Dictionary<Address, List<ItemAmountTarget>>();
                                if (!gi.itemsOrderedThisWeekByImporter.TryGetValue(plan.importAddress, out var rows) || rows == null)
                                { rows = new List<ItemAmountTarget>(); gi.itemsOrderedThisWeekByImporter[plan.importAddress] = rows; }
                                ItemAmountTarget? hit = null;
                                foreach (var r in rows) if (r != null && r.itemName == row.ItemName) { hit = r; break; }
                                if (hit == null) rows.Add(new ItemAmountTarget(row.ItemName, got));
                                else hit.targetAmount += got;
                            }
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] transfer {tid}: the weekly ledger for '{row.ItemName}': {ex.GetType().Name}: {ex.Message}"); }
                        booked += got;
                    }

                    if (left > 0)
                    {
                        // :306-337, the game's own refund and its own message.
                        // F4: native also does ImporterAmountPerItem -= remainder there. It is NOT reproduced
                        // here on purpose: that static is a per-GROUP scratch table, cleared at :84 and consumed
                        // at :99-100 within the one pass, so it is long gone by the time a routed ack arrives -
                        // subtracting into it would either touch an unrelated group's figures or nothing at all.
                        float money = left * row.PricePerUnit * urgent;
                        try
                        {
                            var data = new Dictionary<string, string>
                            {
                                { "itemQuantityFormat", LocalizationHelper.GetItemLabel(row.ItemName, left).ToString() },
                                { "warehouseName", warehouseName },
                                { "businessName", importReg != null ? importReg.BusinessName : "" },
                            };
                            GameManager.ChangeMoneySafe(money, new TransactionInfo(TxRefund, data), null, destReg != null ? destReg.Address : null);
                            var md = new Dictionary<string, string>
                            {
                                { "itemName", row.ItemName.GetLocalization() },
                                { "businessName", warehouseName },
                            };
                            contact?.SendMessage(new TextMessage(MsgNoSpace, md), notify: false);
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] transfer {tid}: the refund for '{row.ItemName}': {ex.GetType().Name}: {ex.Message}"); }
                        refundedUnits += left; refunded += money;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] transfer {tid}: the acknowledgement could not be booked - {ex.GetType().Name}: {ex.Message}"); }

            Note($"transfer {tid} booked {booked} (remainder {refundedUnits})"
               + (refundedUnits > 0 ? $" refunded ${refunded:0.##}" : "") + ".");
            _pending.Remove(tid);
            MarkClosed(tid, t.Day, t.Hour);
            SendClosed(p);
        }

        private static void SendClosed(ImportTransferPayload p)
            => Send(new ImportTransferPayload
            {
                Action = ActClosed, TransferId = p.TransferId ?? "", PlanId = p.PlanId ?? "",
                ImporterKey = p.ImporterKey ?? "", DestKey = p.DestKey ?? "", Day = p.Day, Hour = p.Hour,
            });

        // ══════════ F2  THE RE-OFFER: a PAID row keeps being offered until it exits ══════════

        /// <summary>The GAME hour the hourly sweep last re-offered in; -1 while no paid row is waiting.</summary>
        private static int _lastResendHour = -1;
        private static bool _wasSettled;
        private static int _resendLogBudget = 20;                 // per session, so a long wait cannot flood the log
        private static int _staleLogBudget = 10;                  // per session: the once-a-game-day "still unconfirmed" line
        private static readonly Dictionary<string, int> _staleLoggedDay = new();   // tid -> the game day it last reported
        private static bool _rewindChecked;                       // M4: the rewind prune runs once per loaded world
        /// <summary>Fold 3 (re-check M3): ids this machine WROTE a mark for since the world was loaded. They are by
        /// definition not 'from an abandoned future', whatever the minting machine's clock said: a deliver can be
        /// applied inside MPWorldReady's 3 s settle window, before the prune runs, and its id carries the PLAN
        /// OWNER's hour, which may be one ahead of a trailing clock here. Cleared with the world (ResetSession).</summary>
        private static readonly HashSet<string> _writtenThisWorld = new HashSet<string>();

        /// <summary>MAIN THREAD, 1 Hz, chained off MergerFlip.Tick (where CompanyBooks/CompanyFeed/CampaignMirror
        /// already turn a state into an EDGE). Two triggers, both authoritative - never a blind timer:
        /// (a) the moment this machine's MP session becomes SETTLED again, which is the (re)connect/world-ready
        ///     edge CompanyBooks.Tick keys its own republish on; and
        /// (b) once per GAME hour while a paid row is still waiting.
        /// Cheap outs first: an empty table costs one Count test.</summary>
        internal static void Tick()
        {
            try
            {
                bool settled = MPWorldReady.IsSettled;
                bool becameSettled = settled && !_wasSettled;
                _wasSettled = settled;
                // M4 REWIND (fold 2): the FIRST settled edge of a world is the earliest moment this machine's
                // clock is the loaded save's own - the marks themselves were read back long before it. Ahead of
                // the count test, because applied and closed rows must be pruned even with no paid row waiting.
                if (becameSettled && !_rewindChecked) _rewindChecked = DropMarksFromALaterTime();   // fold 3 (L1): armed until it really compared against a clock
                if (_pending.Count == 0) { _lastResendHour = -1; return; }
                if (becameSettled) { Resend("the session settled", skipSameHour: false); _lastResendHour = HourNow(); return; }
                if (!settled) return;
                int hour = HourNow();
                if (hour == _lastResendHour) return;
                _lastResendHour = hour;
                Resend("the game hour turned", skipSameHour: true);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] the re-offer tick failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>MAIN THREAD. Re-send the deliver leg of every row this machine has PAID for and not yet had
        /// an acknowledgement back for, UNCHANGED in every field - same TransferId, same items, same prices. A
        /// paid row has exactly two exits (booked, or refunded on a full remainder) and neither can be reached
        /// by waiting, so it is offered again until it takes one. Re-sending is safe by construction: the
        /// destination's PERSISTED idempotence re-acks with the identical figures and shelves nothing twice,
        /// and the host re-creates a binding it may have lost. What the host does with the re-sent leg depends
        /// on where the goods are: with nobody ever handed them it answers the refund ack itself, with the
        /// same machine still running the destination it relays again (and that machine re-acks), and with a
        /// DIFFERENT machine - or none - there now it HOLDS the leg until the original one is back. A row can
        /// therefore wait a long time, which is what the once-a-game-day line below reports.</summary>
        internal static void ResendUnacked(string trigger) => Resend(trigger, skipSameHour: false);

        private static void Resend(string trigger, bool skipSameHour)
        {
            try
            {
                if (_pending.Count == 0) return;
                int day = DayNow(), hour = HourNow();
                // The keys are copied first: a leg whose destination is this very machine would be handled
                // synchronously and could close the row mid-enumeration.
                var ids = new List<string>(_pending.Keys);
                foreach (var tid in ids)
                {
                    if (!_pending.TryGetValue(tid, out var t) || t == null || t.Items.Count == 0) continue;
                    // The HOURLY sweep never re-offers a row in the game hour it was first sent - its first
                    // deliver leg is still crossing the wire. An explicit trigger (the session settling, the dev
                    // lever) does, because that is exactly the moment a leg may have been lost.
                    if (skipSameHour && t.Day == day && t.Hour == hour) continue;
                    // H1b (fold 2): a row still waiting more than a game DAY later is stuck on the machine the
                    // goods were handed to (the host holds such a leg rather than shelving them twice). Say so
                    // once per game day, budgeted, so a stuck row is diagnosable from the log alone.
                    if (day - t.Day > 1 && _staleLogBudget > 0
                        && (!_staleLoggedDay.TryGetValue(tid, out var saidOn) || saidOn != day))
                    {
                        _staleLoggedDay[tid] = day; Trim(_staleLoggedDay); _staleLogBudget--;
                        Plugin.Logger.LogWarning($"[Import] transfer {tid}: paid on day {t.Day}, still unconfirmed on day {day} - waiting for the machine that was given the goods.");
                    }
                    var leg = new ImportTransferPayload
                    {
                        Action = ActDeliver, TransferId = tid, PlanId = t.PlanId, ImporterKey = t.ImporterKey,
                        DestKey = t.DestKey, Day = t.Day, Hour = t.Hour, IsTarget = t.IsTarget,
                    };
                    foreach (var b in t.Items)
                        leg.Items.Add(new ImportTransferItem { ItemName = b.ItemName, Amount = b.Amount, PricePerUnit = b.PricePerUnit });
                    if (_resendLogBudget > 0)
                    {
                        _resendLogBudget--;
                        Plugin.Logger.LogInfo($"[Import] transfer {tid}: paid and unacknowledged - deliver offered again ({trigger}).");
                    }
                    Send(leg);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] the paid rows could not be re-offered: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ══════════ PERSISTENCE (the cargo-marks file's import sections) ══════════

        private const int MarkKeep = 500;

        private static void MarkApplied(string tid, ImportTransferPayload ack)
        {
            if (string.IsNullOrEmpty(tid) || ack == null) return;
            _applied[tid] = ack; _writtenThisWorld.Add(tid); Trim(_applied); Prune(_applied); PersistMarks();
        }

        private static void MarkClosed(string tid, int day, int hour)
        {
            if (string.IsNullOrEmpty(tid)) return;
            _closed[tid] = new MarkTime(day, hour); _writtenThisWorld.Add(tid);
            if (_closed.Count > MarkKeep)
            {
                var keys = new List<string>(_closed.Keys);
                keys.Sort((a, b) => _closed[b].Day.CompareTo(_closed[a].Day));
                for (int i = MarkKeep; i < keys.Count; i++) _closed.Remove(keys[i]);
            }
            PersistMarks();
        }

        private static void Prune(Dictionary<string, ImportTransferPayload> d)
        {
            if (d.Count <= MarkKeep) return;
            var keys = new List<string>(d.Keys);
            keys.Sort((a, b) => (d[b] != null ? d[b].Day : 0).CompareTo(d[a] != null ? d[a].Day : 0));
            for (int i = MarkKeep; i < keys.Count; i++) d.Remove(keys[i]);
        }

        private static void PersistMarks()
        {
            try { MPSaveCoordinator.PersistCargoMarksNow(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] the import marks could not be written - {ex.GetType().Name}: {ex.Message}"); }
        }

        internal static List<MpCargoMarkEntry> SnapshotPending()
        {
            var list = new List<MpCargoMarkEntry>();
            try
            {
                foreach (var kv in _pending)
                {
                    if (kv.Value == null) continue;
                    string json = "";
                    try { json = Newtonsoft.Json.JsonConvert.SerializeObject(kv.Value); } catch { }
                    if (json.Length == 0) continue;
                    list.Add(new MpCargoMarkEntry { TransferId = kv.Key, Day = kv.Value.Day, Hour = kv.Value.Hour, PayloadJson = json });
                }
            }
            catch { }
            list.Sort((x, y) => string.CompareOrdinal(x.TransferId, y.TransferId));
            return list;
        }

        internal static List<MpCargoMarkEntry> SnapshotApplied()
        {
            var list = new List<MpCargoMarkEntry>();
            try
            {
                foreach (var kv in _applied)
                {
                    if (kv.Value == null) continue;
                    string json = "";
                    try { json = Newtonsoft.Json.JsonConvert.SerializeObject(kv.Value); } catch { }
                    if (json.Length == 0) continue;
                    list.Add(new MpCargoMarkEntry { TransferId = kv.Key, Day = kv.Value.Day, Hour = kv.Value.Hour, PayloadJson = json });
                }
            }
            catch { }
            list.Sort((x, y) => string.CompareOrdinal(x.TransferId, y.TransferId));
            return list;
        }

        internal static List<MpCargoMarkEntry> SnapshotClosed()
        {
            var list = new List<MpCargoMarkEntry>();
            try { foreach (var kv in _closed) list.Add(new MpCargoMarkEntry { TransferId = kv.Key, Day = kv.Value.Day, Hour = kv.Value.Hour }); } catch { }
            list.Sort((x, y) => string.CompareOrdinal(x.TransferId, y.TransferId));
            return list;
        }

        /// <summary>ADDITIVE, exactly like CargoTransfer's: a restore is a UNION.</summary>
        internal static void RestoreLocalState(List<MpCargoMarkEntry> pending, List<MpCargoMarkEntry> applied, List<MpCargoMarkEntry> closed)
        {
            try
            {
                if (pending != null)
                    foreach (var e in pending)
                    {
                        if (e == null || string.IsNullOrEmpty(e.TransferId) || string.IsNullOrEmpty(e.PayloadJson)) continue;
                        if (_pending.ContainsKey(e.TransferId)) continue;
                        try
                        {
                            var row = Newtonsoft.Json.JsonConvert.DeserializeObject<Pending>(e.PayloadJson);
                            if (row != null) { if (row.Items == null) row.Items = new List<ImportTransferItem>(); _pending[e.TransferId] = row; }
                        }
                        catch { }
                    }
                if (applied != null)
                    foreach (var e in applied)
                    {
                        if (e == null || string.IsNullOrEmpty(e.TransferId) || string.IsNullOrEmpty(e.PayloadJson)) continue;
                        if (_applied.ContainsKey(e.TransferId)) continue;
                        try
                        {
                            var ack = Newtonsoft.Json.JsonConvert.DeserializeObject<ImportTransferPayload>(e.PayloadJson);
                            if (ack != null) _applied[e.TransferId] = ack;
                        }
                        catch { }
                    }
                if (closed != null)
                    foreach (var e in closed)
                        if (e != null && !string.IsNullOrEmpty(e.TransferId)) _closed[e.TransferId] = MintedAt(e.TransferId, e.Day, e.Hour);
                Prune(_applied);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] the import marks could not be restored - {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>M4 (fold 2): when one mark was minted. The transfer id carries it - planId|destKey|day|hour
        /// - and is preferred over the stored figures, because rows persisted before fold 2 kept no hour at
        /// all; the stored day/hour are the fallback for any id that does not parse.</summary>
        private static MarkTime MintedAt(string tid, int day, int hour)
        {
            try
            {
                var f = (tid ?? "").Split('|');
                if (f.Length >= 2 && int.TryParse(f[f.Length - 2], out var d) && int.TryParse(f[f.Length - 1], out var h))
                    return new MarkTime(d, h);
            }
            catch { }
            return new MarkTime(day, hour);
        }

        /// <summary>MAIN THREAD, once per loaded world (M4 REWIND). This marks file lives OUTSIDE the .hsg and
        /// is keyed by SESSION NAME, so loading an EARLIER save of the same session keeps rows minted in a
        /// future that no longer exists. That is not cosmetic: the id is planId|destKey|day|hour, so replaying
        /// that day mints the very same id and Begin skips it as already closed (the original silent-skip
        /// symptom), while the destination's applied-table would re-acknowledge stale figures. Every pending,
        /// applied or closed row whose own (day, hour) is LATER than the loaded world's is therefore dropped -
        /// a PAID pending row included, because its charge was rewound with the save itself.
        /// WHERE THIS RUNS: the MPWorldReady settled edge, NOT the restore call. The restore happens in
        /// MPSaveCoordinator.ProceedWithLoadData before the .hsg is even written to disk, so the clock there
        /// is still the OLD world's; the settled edge is by definition after the new world is up, which is why
        /// SaveGameManager.Current.Day/Hour can be trusted here.</summary>
        private static bool DropMarksFromALaterTime()
        {
            try
            {
                int day = DayNow(), hour = HourNow();
                if (day <= 0) return false;               // no clock to compare against - never prune on a guess; try again at the next edge
                int dropped = 0;
                foreach (var tid in new List<string>(_pending.Keys))
                {
                    _pending.TryGetValue(tid, out var row);
                    if (_writtenThisWorld.Contains(tid)) continue;
                    if (!MintedAt(tid, row != null ? row.Day : 0, row != null ? row.Hour : 0).LaterThan(day, hour)) continue;
                    _pending.Remove(tid); _started.Remove(tid); dropped++;
                }
                foreach (var tid in new List<string>(_applied.Keys))
                {
                    _applied.TryGetValue(tid, out var ack);
                    if (_writtenThisWorld.Contains(tid)) continue;
                    if (!MintedAt(tid, ack != null ? ack.Day : 0, ack != null ? ack.Hour : 0).LaterThan(day, hour)) continue;
                    _applied.Remove(tid); dropped++;
                }
                foreach (var tid in new List<string>(_closed.Keys))
                {
                    if (_writtenThisWorld.Contains(tid)) continue;
                    if (!_closed.TryGetValue(tid, out var when) || !when.LaterThan(day, hour)) continue;
                    _closed.Remove(tid); dropped++;
                }
                if (dropped == 0) return true;
                PersistMarks();
                Plugin.Logger.LogWarning($"[Import] dropped {dropped} mark(s) from a later point in time than the loaded save (day {day} h{hour}).");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] the rewind check failed: {ex.GetType().Name}: {ex.Message}"); return true; }
        }

        /// <summary>The per-WORLD state dies with the old world (CargoTransfer.ResetSession's twin).</summary>
        internal static void ResetSession()
        {
            _pending.Clear(); _started.Clear(); _applied.Clear(); _closed.Clear(); _urgentHint.Clear(); _lastLine = "";
            _staleLoggedDay.Clear();
            _lastResendHour = -1; _wasSettled = false; _resendLogBudget = 20;   // F2: the re-offer edge re-arms with the new world
            _writtenThisWorld.Clear();
            _staleLogBudget = 10; _rewindChecked = false;                       // fold 2: and so do the day-old line and the rewind check
        }

        // ══════════ THE ONE INBOUND SEAM ══════════

        /// <summary>MAIN THREAD. Which half of the transfer this machine is playing is decided by the leg
        /// itself, never guessed: the "need" ASK and "deliver" are the warehouse runner's, the "need" REPLY
        /// and "ack" are the plan owner's, and "closed" only ever travels member -> host.</summary>
        public static void Receive(ImportTransferPayload p)
        {
            try
            {
                if (p == null) return;
                switch (p.Action)
                {
                    case ActNeed:    if (p.Answer) OnNeedAnswer(p); else AnswerNeed(p); break;
                    case ActDeliver: ApplyDeliver(p); break;
                    case ActAck:     OnAck(p); break;
                    case ActClosed:
                        Plugin.Logger.LogWarning($"[Import] transfer {p.TransferId} refused: 'closed' is the host's leg and never travels to a member (sender '{p.PlayerId}').");
                        break;
                    default:
                        Plugin.Logger.LogWarning($"[Import] transfer {p.TransferId} refused: unknown leg '{p.Action}'.");
                        break;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] Receive: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    // ══════════ I1: the two patches on the native pass ══════════

    /// <summary>H-MERGERIMPORT-1. The DETACH sits on the same method the authority veil wraps
    /// (Entities.ImportPartnership.DoDeliveries). Order between the two does not matter: the detach reads the
    /// mod's own flip TABLE, never RentedByPlayer, so it decides the same thing whether the veil's Prefix has
    /// already run or not. The Finalizer re-attaches UNCONDITIONALLY - a native throw must not eat a plan's
    /// products - and only then starts the routed legs.</summary>
    [HarmonyPatch(typeof(ImportPartnership), "DoDeliveries")]
    public static class Patch_ImportPartnership_DetachPartnerLines
    {
        static void Prefix(ImportPartnership __instance, ref ImportTransfer.DetachState __state)
        {
            __state = new ImportTransfer.DetachState();
            try { ImportTransfer.Detach(__instance, __state); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] detach prefix: {ex.GetType().Name}: {ex.Message}"); }
        }

        static Exception Finalizer(ImportPartnership __instance, ImportTransfer.DetachState __state, Exception __exception)
        {
            try { ImportTransfer.Reattach(__instance, __state); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Import] detach finalizer: {ex.GetType().Name}: {ex.Message}"); }
            return __exception;
        }
    }
}
