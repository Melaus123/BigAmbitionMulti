using Streets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Buildings;
using Helpers;
using Entities;
using Enums;

namespace BigAmbitionsMP
{
    /// <summary>T-50 (2026-11-13; protocol 28, unreleased - additive), fold of the independent review: the HOST's ledger of
    /// ONE building sale - escrow + two-phase commit. Written into the manifest (MpManifest.BuildingSales) the moment any
    /// leg changes. The legs, in order: Charged (the buyer's own game took the money - escrow), Released (the seller's
    /// game gave the building and its own listing up), DeedMoved (the host's deed ledger names the buyer), Applied (the
    /// buyer's game added the building), Paid (the seller's game was paid from escrow). BuyerSaved / SellerSaved: that
    /// party's save the host holds was taken after its legs. Retired = every leg confirmed AND saved; a retired record is
    /// kept only while it carries SellerStayed (the rent rule, RentGoesToDeedHolder). Pids are this session's; the
    /// stable ids resolve the parties in a later session.</summary>
    public class MpBuildingSaleEntry
    {
        public string SaleId       { get; set; } = "";
        public string AddressKey   { get; set; } = "";
        public string Name         { get; set; } = "";
        public string BuyerPid     { get; set; } = "";
        public string BuyerStable  { get; set; } = "";
        public string SellerPid    { get; set; } = "";
        public string SellerStable { get; set; } = "";
        public float  Amount       { get; set; }
        public float  Occupancy    { get; set; }
        public float  PricePerSqm  { get; set; }
        public int    Day          { get; set; }
        public bool   Charged      { get; set; }
        public bool   Released     { get; set; }
        public bool   DeedMoved    { get; set; }
        public bool   Applied      { get; set; }
        public bool   Paid         { get; set; }
        public bool   BuyerSaved   { get; set; }
        public bool   SellerSaved  { get; set; }
        public bool   SellerStayed { get; set; }
        public bool   Retired      { get; set; }
        // T-50 fold 2 (F2): a money leg is recorded done in the SAME manifest write that carries the party's post-leg cash
        // in the store its money is restored from (its slot's figure, or a merged party's company wallet): *Stamped =
        // that write carried it, so a restart never re-runs the leg. Unstamped = no such store existed (the host before
        // its first slot), so its money comes back from its own save and the leg follows that save's tagged line.
        public bool   ChargeStamped { get; set; }
        public bool   PayStamped    { get; set; }
        public int    ChargedDay    { get; set; }     // the game day the escrow was taken - the refund deadline counts from it
        public bool   Cancelled     { get; set; }     // fold 2 (c): the seller never released (deadline, ban, deed lost) - the escrow goes back
        public bool   Refunded      { get; set; }     // the refund leg returned the escrow to the buyer (a money leg)
        public bool   RefundStamped { get; set; }
    }

    /// <summary>T-50 owner decision 37: one day's rent a PLAYER tenant paid natively for a building another player holds
    /// the deed of. Held (MpManifest.RentForwards) until the deed holder's game books it, then kept as a recorded id
    /// (MpManifest.RentDone) for at least 7 game days so a repeated report never moves money twice (fold F6).
    /// Saved = the host itself is the payee and its own save was taken after it booked the rent.</summary>
    public class MpRentForwardEntry
    {
        public string RentId      { get; set; } = "";
        public string AddressKey  { get; set; } = "";
        public string PayerPid    { get; set; } = "";
        public string PayeePid    { get; set; } = "";
        public string PayeeStable { get; set; } = "";
        public float  Amount      { get; set; }
        public int    Day         { get; set; }
        public bool   Saved       { get; set; }
    }

    /// <summary>
    /// T-50: players buy BUILDINGS (deed + rent income, never vehicles - owner decision 35) from each other.
    ///  - The offer: the game's own buy box (BizManPresentation.SendBuyBuildingOffer, decompile :889) on a building another
    ///    player holds the deed of reaches T-33's claim; the host's deed check (MPServer.TryClaimDeed) finds the holder and,
    ///    instead of refusing, makes a hub offer (LoanOffer Kind="building") to that owner (HostOfferFromBuy). An OFFLINE
    ///    owner is refused up front (decision 36); a Hamptons house keeps T-33's refusal (decision 38).
    ///  - The accept (MPHub.HostHandleAnswer): both players online, the deed ledger still names the seller, no earlier
    ///    sale of the address still open (fold F3). HostBegin records the sale; NOTHING has moved yet.
    ///  - The legs (fold, ROOT): the host is the ONE ledger. HostTick sends each leg the ledger has NOT recorded, in order,
    ///    every 5 s until confirmed: "charge" to the buyer (its own game takes the money as the game's own buy does -
    ///    ba:transaction_buildingbought, never below zero: no money = the game's own insufficient-money notice and the
    ///    offer goes back to pending); "release" to the seller (the game's own computer-buy removal, RealEstateHelper.cs
    ///    :136-149, minus the payment, and its own listing cancelled - fold F4); the host then moves the deed; "finalize"
    ///    to the buyer (the game's own buy, BizManPresentation.cs:925-962, minus the charge, with the seller's occupancy
    ///    and rent price); "pay" to the seller (ba:transaction_buildingsold, from escrow).
    ///  - Money once (fold 2, F2): a money leg is decided from the SAME store the party's money is restored from. Its
    ///    confirmation carries the new balance (a guest's recorded as its live figure on receipt, MPServer, same ordered
    ///    stream as CashSync; the host's own recorded in HostOnMessage), and the leg is recorded in the ledger in the same
    ///    manifest write that stamps that figure into the party's slot (or carries a merged party's wallet): *Stamped. A
    ///    stamped leg is never re-run. Only where no such store exists (the host before its first slot) does the money
    ///    come back from the party's own save - then, and only then, the leg follows that save's tagged transaction line.
    ///  - Refund (fold 2, c): escrow with a deadline. The seller has not released within RefundAfterDays game days of the
    ///    charge, is banned, or no longer holds the deed (the host's ledger, or its own game - F4): the host cancels, the
    ///    escrow goes back to the buyer once on a ledger-tracked "refund" leg (same money-once rule), the deed stays with
    ///    the seller and the address unblocks.
    ///    A BUILDING leg is idempotent on the game's own list and is replayed at each party's world-ready until that
    ///    party's save covers it - it never moves money.
    ///  - Rent (decisions 37 and 51 b, RentGoesToDeedHolder): the payer's game reports each native rent charge
    ///    (RunDaily postfix) with an id; the host records the id (kept >= 7 game days) and the deed holder's game books
    ///    it once as ba:transaction_rentrevenue (RealEstateHelper.cs:80-81).
    /// </summary>
    internal static class MPBuildingSale
    {
        internal const string Kind = "building";
        private const string TagKey = "bampId";   // the sale/rent id in the native Transaction's data (txlines; the host's own proof)
        private const float ResendSeconds = 5f;
        private const int RentIdKeepDays = 7;      // fold F6: the recorded rent ids outlive the game's 6-day transaction list
        /// <summary>Fold 2 (c): game days after the buyer's charge within which the seller must release; then the host
        /// cancels the sale and refunds the escrow. The seller's game applies a first release only before the day BEFORE
        /// that deadline (SellerRelease), so a release and the cancel never both happen across a midnight the two clocks
        /// cross a moment apart.</summary>
        internal const int RefundAfterDays = 3;   // owner decision 53 (2026-11-13)
        internal static int DevRefundAfterDays = -1;   // DEV lever xferdeadline (set only by TestDrive): -1 = RefundAfterDays
        private static int RefundDays => DevRefundAfterDays >= 0 ? DevRefundAfterDays : RefundAfterDays;
        internal static string DevDropOnce = "";       // DEV lever xferdrop <reply>: this game applies the leg, its confirmation is lost once
        private static readonly HashSet<string> _windowLogged = new(StringComparer.Ordinal);   // seller: sales whose closed release window was logged

        // Owner-approved wording (decision 40, 2026-11-13); the offer-sent line is the existing one (MPPatches.cs:9399).
        internal static string OfferLine(string address) => $"{address}: the building and its rent income";
        internal static string OfflineText(string owner) => $"{owner} is offline, so they can't get your offer right now.";
        internal static string OfferSentText(string owner) => $"Offer sent to {owner}. Track it in the Business app.";   // decision 54 (2026-11-13)
        internal static string AcceptHeldOfflineText(string player) => $"{player} is offline, so the sale can't finish right now. The offer stays in your inbox.";   // decision 50 (2026-11-13)
        internal static string RefundedText(string seller, string address) => $"{seller} didn't complete the sale of {address}, so your money has been returned.";   // decision 53 (2026-11-13)

        private static readonly object _lock = new();
        private static readonly List<MpBuildingSaleEntry> _sales = new();     // HOST, under _lock
        private static readonly List<MpRentForwardEntry> _rents = new();      // HOST, under _lock: held for the deed holder
        private static readonly List<MpRentForwardEntry> _rentDone = new();   // HOST, under _lock: recorded ids (fold F6)
        private static readonly Dictionary<string, int> _confirmGen = new(StringComparer.Ordinal);   // HOST, under _lock: "sale|b" / "sale|s" -> save round of the party's last confirmation (int.MaxValue = a replay awaits it)
        private static int _saveGen;                                            // HOST, under _lock: coordinated-save rounds this session
        private static float _nextSendAt;                                       // HOST, main thread
        private static readonly HashSet<string> _rentNoted = new(StringComparer.Ordinal);   // payer: ids the host recorded
        private static readonly HashSet<string> _moneyHere = new(StringComparer.Ordinal);   // party: money legs THIS loaded world applied
        private static object? _moneyWorld;                                     // the GameInstance _moneyHere belongs to
        private static bool _unsettledLogged, _overlayLogged;
        internal static string DevHold = "";   // DEV lever xferhold <leg>: this game neither applies nor confirms that leg

        internal static void Reset()
        {
            lock (_lock) { _sales.Clear(); _rents.Clear(); _rentDone.Clear(); _confirmGen.Clear(); _saveGen = 0; }
            _rentNoted.Clear();
            _moneyHere.Clear(); _moneyWorld = null;
            DevHold = ""; DevDropOnce = ""; DevRefundAfterDays = -1; _windowLogged.Clear();
        }

        // ── shared predicates ─────────────────────────────────────────────────
        internal static bool LedgerNames(string ledgerVal, string pid, string stable)
            => !string.IsNullOrEmpty(ledgerVal)
               && ((!string.IsNullOrEmpty(pid) && ledgerVal == pid)
                   || (ledgerVal == "host" && pid == MPConfig.PlayerId)
                   || (!string.IsNullOrEmpty(stable) && ledgerVal == stable));

        /// <summary>True iff ANOTHER player holds this building's deed: the host reads its deed ledger, a guest the deed
        /// stamp the host's ownership sync writes for another player's building (Protocol.cs:2833-2834).</summary>
        internal static bool IsOtherPlayersDeed(BuildingRegistration? reg)
        {
            try
            {
                if (reg == null || (!MPServer.IsRunning && !MPClient.IsConnected)) return false;
                string key = GameStateReader.AddressKey(reg);
                if (MPServer.IsRunning)
                {
                    if (!MPServer.BuildingRealEstateOwners.TryGetValue(key, out var d) || string.IsNullOrEmpty(d)) return false;
                    return !LedgerNames(d, MPConfig.PlayerId, MPConfig.StableId);
                }
                string stamp = ""; try { stamp = reg.buildingOwnerRivalId?.ToString() ?? ""; } catch { }
                return stamp.Length > 0 && stamp != MPConfig.PlayerId && GameStatePatcher.IsSessionPlayerRivalId(stamp);
            }
            catch { return false; }
        }

        /// <summary>The ONE rule for whose rent goes to a player deed holder (decision 51 b, 2026-11-13): anyone running a
        /// business in a building ANOTHER player holds the deed of pays that player the rent (the caller has already
        /// excluded a deed the payer holds itself).</summary>
        internal static bool RentGoesToDeedHolder(string addr, string payerPid, string holder) => true;

        /// <summary>HOST: a sale of this address that is not yet retired (fold F3: a new sale of it waits until then).</summary>
        internal static bool HasOpenSale(string addr)
        {
            lock (_lock) return _sales.Exists(e => !e.Retired && e.AddressKey == addr);
        }

        private static bool IsHostRole(string pid, string stable)
            => (!string.IsNullOrEmpty(stable) && stable == MPConfig.StableId) || pid == MPConfig.PlayerId || pid == "host";

        private static string Fmt(BuildingRegistration reg, string fallback)
        {
            try { return reg.Address.ToFormattedString(); } catch { return fallback; }
        }

        private static string ResolvePid(string pid, string stable)
        {
            if (!string.IsNullOrEmpty(stable) && stable == MPConfig.StableId) return MPConfig.PlayerId;
            string p = "";
            try { p = MPServer.PidOf(stable); } catch { }
            return string.IsNullOrEmpty(p) ? (pid ?? "") : p;
        }

        private static bool HasTagIn(string id)
        {
            try
            {
                var q = SaveGameManager.Current?.Transactions;
                if (q == null) return false;
                foreach (var t in q)
                    if (t?.transactionData != null && t.transactionData.TryGetValue(TagKey, out var v) && v == id) return true;
            }
            catch { }
            return false;
        }

        /// <summary>A party: did THIS game already move the money of that leg? This loaded world's own record (a re-send
        /// that crossed the confirmation, or a lost confirmation); on the HOST also its own tagged transaction line. The
        /// host re-sends its own money leg after a restart ONLY when the ledger holds it unstamped - i.e. its money came back
        /// from its own save, beside that line (fold 2): a stamped leg is never re-sent. A guest's money after a rejoin is
        /// the host's figure, never its save, so a guest's rolled-back lists never decide it - the host's ledger does.</summary>
        private static bool MoneyAlreadyHere(string tag)
        {
            var gi = SaveGameManager.Current;
            if (!ReferenceEquals(_moneyWorld, gi)) { _moneyWorld = gi; _moneyHere.Clear(); }
            if (_moneyHere.Contains(tag)) return true;
            return MPServer.IsRunning && HasTagIn(tag);
        }

        private static void RefreshPoi(BuildingRegistration reg)
        {
            try { InstanceBehavior<CityManager>.Instance?.FindCityBuildingController(reg.Address)?.UpdatePoi(); } catch { }
            try { InstanceBehavior<UI.UIs>.Instance?.mapFilters.ApplyFilters(); } catch { }
        }

        /// <summary>Show one approved line and log exactly what was shown (scenarios assert the shown text).</summary>
        private static void ShowNote(UI.Notification.NotificationType type, string text)
        {
            try { UI.Notification.Notifications.Show(type, text); } catch { }
            Plugin.Logger.LogInfo($"[BldSale] shown: {text}");
        }

        // ── HOST: the offer, from T-33's deed check ───────────────────────────
        /// <summary>HOST (any thread): the buy of a building whose deed ANOTHER player (holder, a ledger value) holds.
        /// "offer" = a hub offer was made to the owner (made on the main thread); "offline" = the owner is not in the
        /// session (decision 36); "refused" = a Hamptons house (decision 38: T-33's refusal stands).</summary>
        internal static string HostOfferFromBuy(string addr, string buyerPid, string holder, double amount, BuyClaimPayload? answer, out string ownerName)
        {
            var lobby = MPRestSync.AllPlayers();
            string ownerPid = holder == "host" ? MPConfig.PlayerId : holder;
            if (!lobby.Contains(ownerPid)) { string p2 = ""; try { p2 = MPServer.PidOf(holder); } catch { } if (!string.IsNullOrEmpty(p2)) ownerPid = p2; }
            bool online = lobby.Contains(ownerPid);
            ownerName = ownerPid;
            if (!online)
            {
                string n = ""; try { n = GrantSync.NameOf(holder); } catch { }
                if (!string.IsNullOrEmpty(n)) ownerName = n;
            }
            bool hamptons = false;
            try { var reg = GameStatePatcher.FindRegistration(addr); hamptons = reg?.BuildingCached != null && reg.BuildingCached.IsHamptonsHouse(); } catch { }
            if (hamptons)
            {
                Plugin.Logger.LogWarning($"[BldSale] buy of '{addr}' by {buyerPid} @ {amount:F0} REFUSED - the deed is held by {holder} and Hamptons houses are not sold between players (no offer, no money moved).");
                if (answer != null) { answer.Granted = false; answer.OwnedByPlayer = true; }
                return "refused";
            }
            if (!online)
            {
                Plugin.Logger.LogWarning($"[BldSale] offer on '{addr}' by {buyerPid} @ {amount:F0} REFUSED - the owner {ownerName} is offline (no offer sent, no money moved).");
                if (answer != null) { answer.Granted = false; answer.HolderOffline = true; answer.Holder = ownerName; }
                return "offline";
            }
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            if (answer != null) { answer.Granted = false; answer.OfferSent = true; answer.OfferId = id; answer.Holder = ownerPid; }
            float amt = (float)amount;
            string to = ownerPid;
            // answer == null = the HOST's own buy (MPPatches.Patch_SendBuyBuildingOffer, main thread): the offer is stored now,
            // so the buy box returns with it in the host's outgoing list; a guest's claim (any thread) is queued.
            if (answer == null) MPHub.HostCreateBuildingOffer(id, buyerPid, to, amt, addr);
            else GameStatePatcher.EnqueueOnMainThread(() => MPHub.HostCreateBuildingOffer(id, buyerPid, to, amt, addr));
            Plugin.Logger.LogInfo($"[BldSale] offer {id} on '{addr}' by {buyerPid} @ {amount:F0} SENT to the deed holder {ownerPid} (no money moves unless they accept).");
            return "offer";
        }

        /// <summary>The buyer's screen after the deed check (host-as-buyer path; a guest uses GuestOnOfferAnswer).</summary>
        internal static void ShowBuyOutcome(string outcome, string ownerName)
        {
            try
            {
                if (outcome == "offer") ShowNote(UI.Notification.NotificationType.Info, OfferSentText(ownerName));
                else if (outcome == "offline") ShowNote(UI.Notification.NotificationType.Error, OfflineText(ownerName));
                else MPPatches.Patch_SendBuyBuildingOffer.ShowOwnedRefusal();
            }
            catch { }
        }

        /// <summary>GUEST, MAIN THREAD: the host turned my held buy into an offer (or refused it: owner offline).</summary>
        internal static void GuestOnOfferAnswer(BuyClaimPayload a)
        {
            try
            {
                if (a.HolderOffline)
                {
                    Plugin.Logger.LogInfo($"[BldSale] claim {a.ClaimId} for '{a.AddressKey}': the owner {a.Holder} is offline - no offer sent; nothing was charged.");
                    ShowNote(UI.Notification.NotificationType.Error, OfflineText(a.Holder));
                    return;
                }
                string name = a.AddressKey;
                try { var reg = GameStatePatcher.FindRegistration(a.AddressKey); if (reg != null) name = Fmt(reg, a.AddressKey); } catch { }
                MPHub.NoteOutgoingBuildingOffer(new LoanOfferPayload
                {
                    Id = a.OfferId, From = MPConfig.PlayerId, To = a.Holder, Principal = (float)a.Amount,
                    Kind = Kind, AddressKey = a.AddressKey, BusinessName = name,
                });
                Plugin.Logger.LogInfo($"[BldSale] claim {a.ClaimId} for '{a.AddressKey}': offer {a.OfferId} @ {a.Amount:F0} sent to {a.Holder}; nothing was charged.");
                ShowNote(UI.Notification.NotificationType.Info, OfferSentText(a.Holder));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BldSale] offer answer: {ex.Message}"); }
        }

        /// <summary>SELLER, MAIN THREAD, at Accept: read the occupancy + rent price the buyer's record will carry. Nothing
        /// changes here - the host decides first, and the seller's own listing is cancelled inside its "release" leg
        /// (fold F4). No record here = Occupancy -1 (the host lapses the sale).</summary>
        internal static void SellerPrepareAccept(LoanOfferPayload offer, LoanAnswerPayload a)
        {
            try
            {
                var gi = SaveGameManager.Current;
                var reg = GameStatePatcher.FindRegistration(offer.AddressKey ?? "");
                if (gi == null || reg == null) { a.Occupancy = -1f; return; }
                var re = gi.realEstate?.Find(x => x != null && x.address == reg.Address);
                if (re == null) { a.Occupancy = -1f; Plugin.Logger.LogWarning($"[BldSale] accept of offer {offer.Id}: '{offer.AddressKey}' is not in this game's own real-estate list."); return; }
                a.Occupancy = re.occupancy; a.PricePerSqm = re.pricePerSqm;
                Plugin.Logger.LogInfo($"[BldSale] accept of offer {offer.Id} for '{offer.AddressKey}': occupancy {re.occupancy:F1}, rent/m2 {re.pricePerSqm:F2} sent; the host decides.");
            }
            catch (Exception ex) { a.Occupancy = -1f; Plugin.Logger.LogWarning($"[BldSale] accept prepare: {ex.Message}"); }
        }

        internal static bool HostSellerHoldsDeed(string addr, string sellerPid)
        {
            MPServer.BuildingRealEstateOwners.TryGetValue(addr ?? "", out var d);
            return LedgerNames(d ?? "", sellerPid, MPServer.StableOf(sellerPid));
        }

        // ── HOST: the sale ledger ────────────────────────────────────────────
        /// <summary>HOST, MAIN THREAD, from MPHub.HostHandleAnswer once every accept check passed: the sale is recorded and
        /// the manifest written at once. Nothing has moved: the first leg is the buyer's own charge (escrow).</summary>
        internal static void HostBegin(LoanOfferPayload offer, float occupancy, float pricePerSqm)
        {
            string addr = offer.AddressKey ?? "";
            var e = new MpBuildingSaleEntry
            {
                SaleId = offer.Id, AddressKey = addr, Name = offer.BusinessName ?? addr,
                BuyerPid = offer.From, BuyerStable = MPServer.StableOf(offer.From),
                SellerPid = offer.To, SellerStable = MPServer.StableOf(offer.To),
                Amount = offer.Principal, Occupancy = occupancy, PricePerSqm = pricePerSqm,
            };
            try { e.Day = SaveGameManager.Current?.Day ?? 0; } catch { }
            lock (_lock) { _sales.RemoveAll(x => x.Retired && x.AddressKey == addr); _sales.Add(e); }   // a retired record of the address is superseded; an OPEN one never reaches here (HasOpenSale, fold F3)
            MPHub.SaveLedger();
            _nextSendAt = 0f;
            Plugin.Logger.LogInfo($"[BldSale] sale {e.SaleId} LEDGERED: '{addr}' {e.SellerPid} -> {e.BuyerPid} for {e.Amount:F2} (occupancy {occupancy:F1}, rent/m2 {pricePerSqm:F2}); first leg: the buyer's charge; manifest written.");
        }

        private static string Legs(MpBuildingSaleEntry e)
            => $"charged={e.Charged} released={e.Released} deed={e.DeedMoved} applied={e.Applied} paid={e.Paid} buyerSaved={e.BuyerSaved} sellerSaved={e.SellerSaved} cancelled={e.Cancelled} refunded={e.Refunded} stamped={e.ChargeStamped}/{e.PayStamped}/{e.RefundStamped}";

        private static BuildingSalePayload Leg(MpBuildingSaleEntry e, string action) => new BuildingSalePayload
        {
            Action = action, SaleId = e.SaleId, AddressKey = e.AddressKey, Name = e.Name,
            BuyerId = ResolvePid(e.BuyerPid, e.BuyerStable), SellerId = ResolvePid(e.SellerPid, e.SellerStable),
            Amount = e.Amount, Occupancy = e.Occupancy, PricePerSqm = e.PricePerSqm, Day = e.Day,
            // refund: the charge is certainly taken (a guest's - the host's figure carries it - or a stamped one); otherwise
            // the deal is committed once the seller gave the building up (DeedMoved survives a host-seller's reopen)
            Committed = action == "refund" ? (!IsHostRole(e.BuyerPid, e.BuyerStable) || e.ChargeStamped) : (e.Released || e.DeedMoved),
            Deadline = DeadlineDay(e),
            SellerName = action == "refund" ? SellerDisplayName(e) : "",
        };

        /// <summary>HOST: the seller's display name for the buyer's refund notice (the seller may be offline or banned).</summary>
        private static string SellerDisplayName(MpBuildingSaleEntry e)
        {
            string n = ""; try { n = GrantSync.NameOf(e.SellerStable); } catch { }
            return string.IsNullOrEmpty(n) ? ResolvePid(e.SellerPid, e.SellerStable) : n;
        }

        /// <summary>Fold 2 (c): the game day from which the host cancels an unreleased sale (0 = the charge day is unknown).</summary>
        private static int DeadlineDay(MpBuildingSaleEntry e)
        {
            int from = e.ChargedDay > 0 ? e.ChargedDay : e.Day;
            return from > 0 ? from + RefundDays : 0;
        }

        private static bool IsDone(MpBuildingSaleEntry e)
            => e.Cancelled ? e.Refunded && (e.RefundStamped || e.BuyerSaved)
                           : e.Charged && e.Released && e.DeedMoved && e.Applied && e.Paid && e.BuyerSaved && e.SellerSaved;

        private static int SendLeg(MpBuildingSaleEntry e, string action, bool toSeller, IReadOnlyList<string> online)
        {
            var p = Leg(e, action);
            string pid = toSeller ? p.SellerId : p.BuyerId;
            if (pid == MPConfig.PlayerId) { OnMessage(p); return 1; }   // the host's own leg: same apply, same confirm
            if (!online.Contains(pid)) return 0;
            MPServer.SendHubTo(pid, MessageType.BuildingSale, p);
            return 1;
        }

        /// <summary>HOST: send every leg of this sale the ledger has NOT recorded, in order (only to onlyPid when given).
        /// replay = that party just reached world-ready: its confirmed BUILDING legs its save may predate are re-sent too
        /// (building only - the money legs are recorded and are never re-sent).</summary>
        private static int SendPending(MpBuildingSaleEntry e, IReadOnlyList<string> online, string? onlyPid, bool replay)
        {
            string buyer = ResolvePid(e.BuyerPid, e.BuyerStable), seller = ResolvePid(e.SellerPid, e.SellerStable);
            bool toB = onlyPid == null || onlyPid == buyer, toS = onlyPid == null || onlyPid == seller;
            int n = 0;
            if (e.Cancelled) { if (toB && e.Charged && !e.Refunded) n += SendLeg(e, "refund", false, online); return n; }   // fold 2 (c): only the refund is left
            if (!e.Charged) { if (toB) n += SendLeg(e, "charge", false, online); return n; }
            if (toS && (!e.Released || (replay && !e.SellerSaved)))
            {
                if (e.Released) AwaitConfirm(e, true);
                n += SendLeg(e, "release", true, online);
            }
            if (!e.DeedMoved) return n;
            if (toB && (!e.Applied || (replay && !e.BuyerSaved)))
            {
                if (e.Applied) AwaitConfirm(e, false);
                n += SendLeg(e, "finalize", false, online);
            }
            if (toS && !e.Paid) n += SendLeg(e, "pay", true, online);
            return n;
        }

        private static void AwaitConfirm(MpBuildingSaleEntry e, bool seller) { lock (_lock) _confirmGen[e.SaleId + (seller ? "|s" : "|b")] = int.MaxValue; }
        private static void NoteConfirm(MpBuildingSaleEntry e, bool seller) { lock (_lock) _confirmGen[e.SaleId + (seller ? "|s" : "|b")] = _saveGen; }

        /// <summary>HOST: the seller gave the building up - the deed ledger names the buyer's REAL id (never the literal
        /// "host", T-33 / round-159), the building leaves the market, access refreshes.</summary>
        private static void HostMoveDeed(MpBuildingSaleEntry e)
        {
            string buyer = ResolvePid(e.BuyerPid, e.BuyerStable);
            MPServer.BuildingRealEstateOwners[e.AddressKey] = buyer;
            try { GameStatePatcher.HostRemoveFromForSale(e.AddressKey); } catch (Exception ex) { Plugin.Logger.LogWarning($"[BldSale] market removal '{e.AddressKey}': {ex.Message}"); }
            // T-34: the host's own game records the new holder too (a guest buyer; the host's own deed is left to its game)
            GameStatePatcher.HostReflectPlayerDeed(e.AddressKey, "building sale");
            try { MPServer.RefreshBuildingAccess("building sale"); } catch { }
            e.DeedMoved = true;
            Plugin.Logger.LogInfo($"[BldSale] sale {e.SaleId}: the deed ledger names {buyer} for '{e.AddressKey}'.");
        }

        /// <summary>HOST tick (main thread, MPOffers.HostTick): every 5 s, every leg the ledger has not recorded and every
        /// held rent; the recorded rent ids older than 7 game days are dropped.</summary>
        internal static void HostTick()
        {
            if (!MPServer.IsRunning || Time.unscaledTime < _nextSendAt) return;
            _nextSendAt = Time.unscaledTime + ResendSeconds;
            try
            {
                List<MpBuildingSaleEntry> sales; List<MpRentForwardEntry> rents;
                int today = 0; try { today = SaveGameManager.Current?.Day ?? 0; } catch { }
                int pruned = 0;
                lock (_lock)
                {
                    sales = _sales.Where(x => !x.Retired).ToList(); rents = new List<MpRentForwardEntry>(_rents);
                    if (today > 0) pruned = _rentDone.RemoveAll(r => r.Day < today - RentIdKeepDays && (r.Saved || !IsHostRole(r.PayeePid, r.PayeeStable)));
                }
                if (pruned > 0) MPHub.SaveLedger();
                if (sales.Count == 0 && rents.Count == 0) return;
                var online = MPRestSync.AllPlayers();
                foreach (var e in sales)
                {
                    if (e.Released && !e.DeedMoved && !e.Cancelled) { HostMoveDeed(e); MPHub.SaveLedger(); }   // a restart between the two
                    if (!e.Cancelled && e.Charged && !e.Released && !e.DeedMoved)
                    {
                        string why = CancelReason(e, today);
                        if (why.Length > 0) HostCancel(e, why);
                    }
                    SendPending(e, online, null, false);
                }
                foreach (var r in rents)
                {
                    string payee = ResolvePid(r.PayeePid, r.PayeeStable);
                    var p = new BuildingSalePayload { Action = "rent", RentId = r.RentId, AddressKey = r.AddressKey, Amount = r.Amount, Day = r.Day, SellerId = r.PayerPid, BuyerId = payee };
                    if (payee == MPConfig.PlayerId) OnMessage(p);
                    else if (online.Contains(payee)) MPServer.SendHubTo(payee, MessageType.BuildingSale, p);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BldSale] host tick: {ex.Message}"); }
        }

        /// <summary>HOST, fold 2 (c) + F4: why this charged, unreleased sale must be cancelled now ("" = it stands): the
        /// seller is banned (the saved ban list - any ban path, any session), the host's deed ledger no longer names the
        /// seller, or the seller has not released by the deadline.</summary>
        private static string CancelReason(MpBuildingSaleEntry e, int today)
        {
            string seller = ResolvePid(e.SellerPid, e.SellerStable);
            string st = !string.IsNullOrEmpty(e.SellerStable) ? e.SellerStable : (MPServer.StableOf(seller) ?? "");
            try { if (st.Length > 0 && !IsHostRole(e.SellerPid, e.SellerStable) && MPConfig.FindBan("", st, "", out _) != null) return "the seller is banned"; } catch { }
            MPServer.BuildingRealEstateOwners.TryGetValue(e.AddressKey, out var d);
            if (!LedgerNames(d ?? "", seller, st)) return $"the host's deed ledger no longer names the seller ('{d}')";
            int deadline = DeadlineDay(e);
            if (deadline > 0 && today > 0 && today >= deadline) return $"the seller has not released within {RefundDays} game day(s) of the charge (deadline day {deadline}, today {today})";
            return "";
        }

        /// <summary>HOST, MAIN THREAD: cancel a charged sale the seller never released - recorded at once; the refund leg
        /// follows (SendPending). Nothing else moves: the deed stays with the seller.</summary>
        private static void HostCancel(MpBuildingSaleEntry e, string why)
        {
            e.Cancelled = true;
            MPHub.SaveLedger();
            _nextSendAt = 0f;
            Plugin.Logger.LogWarning($"[BldSale] sale {e.SaleId} '{e.AddressKey}': CANCELLED - {why}; the escrow {e.Amount:F2} goes back to the buyer on the refund leg, the deed stays with the seller.");
        }

        /// <summary>HOST (main thread, from MPServer's ban paths): a player was banned - the next tick cancels a sale that
        /// player had not released yet (CancelReason reads the saved ban list).</summary>
        internal static void OnBanned() => _nextSendAt = 0f;

        /// <summary>HOST: a party reached world-ready - its pending legs, its confirmed building legs its save may predate
        /// (fold F1), and every held rent naming it.</summary>
        internal static void HostReplayTo(string pid)
        {
            try
            {
                List<MpBuildingSaleEntry> sales; List<MpRentForwardEntry> rents;
                lock (_lock) { sales = _sales.Where(x => !x.Retired).ToList(); rents = new List<MpRentForwardEntry>(_rents); }
                var online = MPRestSync.AllPlayers();
                int n = 0;
                foreach (var e in sales) n += SendPending(e, online, pid, true);
                foreach (var r in rents)
                    if (ResolvePid(r.PayeePid, r.PayeeStable) == pid)
                    { MPServer.SendHubTo(pid, MessageType.BuildingSale, new BuildingSalePayload { Action = "rent", RentId = r.RentId, AddressKey = r.AddressKey, Amount = r.Amount, Day = r.Day, SellerId = r.PayerPid, BuyerId = pid }); n++; }
                if (n > 0) Plugin.Logger.LogInfo($"[BldSale] world-ready replay to '{pid}': {n} leg(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BldSale] replay to '{pid}': {ex.Message}"); }
        }

        /// <summary>HOST (any thread): a coordinated save round begins (MPSaveCoordinator.HostSaveNow) - a party's save
        /// that lands after it was taken after every confirmation the host had before it.</summary>
        internal static int OnSaveNow() { lock (_lock) return ++_saveGen; }   // the round number rides SaveNow and comes back on each upload (fold 2, d)

        /// <summary>HOST (any thread), just before a party's save is merged into the manifest (the host's own after its
        /// local save; a guest's when its upload is stored): mark the legs that save covers and retire every sale whose
        /// legs are all confirmed AND saved (fold F3). The merge that follows writes the manifest. Fold 2 (d): a guest's
        /// upload covers a leg only when it answers a save round that STARTED after that leg was recorded (round = the
        /// SaveNow round it carries; 0 = unknown: it covers nothing) - an older upload landing late proves nothing.</summary>
        internal static void OnPartySaved(string stableId, int round = 0)
        {
            if (!MPServer.IsRunning || string.IsNullOrEmpty(stableId)) return;
            bool own = stableId == MPConfig.StableId;
            var done = new List<string>();
            lock (_lock)
            {
                bool GenOk(string key) => own ? (!_confirmGen.TryGetValue(key, out var g) || g <= _saveGen)
                                              : (round > 0 && (!_confirmGen.TryGetValue(key, out var h) || h < round));
                foreach (var e in _sales)
                {
                    if (e.Retired) continue;
                    bool isBuyer = e.BuyerStable == stableId || (own && IsHostRole(e.BuyerPid, e.BuyerStable));
                    bool isSeller = e.SellerStable == stableId || (own && IsHostRole(e.SellerPid, e.SellerStable));
                    if (isBuyer && !e.BuyerSaved && e.Charged && e.Applied && GenOk(e.SaleId + "|b")) { e.BuyerSaved = true; done.Add($"{e.SaleId} buyer"); }
                    if (isSeller && !e.SellerSaved && e.Released && e.Paid && GenOk(e.SaleId + "|s")) { e.SellerSaved = true; done.Add($"{e.SaleId} seller"); }
                    if (isBuyer && !e.BuyerSaved && e.Cancelled && e.Refunded && GenOk(e.SaleId + "|b")) { e.BuyerSaved = true; done.Add($"{e.SaleId} buyer (refund)"); }
                    if (IsDone(e)) { e.Retired = true; done.Add($"{e.SaleId} RETIRED"); }
                }
                _sales.RemoveAll(e => e.Retired && !e.SellerStayed);
                if (own) foreach (var r in _rentDone) if (!r.Saved && IsHostRole(r.PayeePid, r.PayeeStable)) r.Saved = true;
            }
            if (done.Count > 0) Plugin.Logger.LogInfo($"[BldSale] save of '{stableId}' covers: {string.Join(", ", done)}.");
        }

        /// <summary>HOST, MAIN THREAD: a confirmation or a rent report from a party (or the host's own legs). A leg the
        /// ledger already recorded changes nothing (a replayed building leg's answer).</summary>
        internal static void HostOnMessage(BuildingSalePayload p, string senderPid)
        {
            if (p == null || !MPServer.IsRunning) return;
            try
            {
                switch (p.Action)
                {
                    case "charged": case "chargefail": case "released": case "releasefail": case "finalized": case "paid": case "refunded":
                    {
                        bool seller = p.Action == "released" || p.Action == "paid" || p.Action == "releasefail";
                        MpBuildingSaleEntry? e;
                        lock (_lock) e = _sales.Find(x => x.SaleId == p.SaleId && !x.Retired);
                        if (e == null) return;
                        string who = seller ? ResolvePid(e.SellerPid, e.SellerStable) : ResolvePid(e.BuyerPid, e.BuyerStable);
                        if (who != senderPid) { Plugin.Logger.LogWarning($"[BldSale] '{p.Action}' for sale {p.SaleId} from '{senderPid}', not the {(seller ? "seller" : "buyer")} '{who}' - dropped."); return; }
                        NoteConfirm(e, seller);
                        bool changed = false, money = false;
                        switch (p.Action)
                        {
                            case "chargefail":
                                if (e.Charged) return;
                                lock (_lock) _sales.Remove(e);
                                MPHub.SaveLedger();
                                Plugin.Logger.LogWarning($"[BldSale] sale {e.SaleId} '{e.AddressKey}': the buyer {senderPid}'s own game cannot pay {e.Amount:F2} - nothing moved; the offer is pending again.");
                                MPHub.HostBuildingOfferOutcome(OfferOf(e), false);
                                return;
                            case "charged":
                                if (!e.Charged)
                                {
                                    e.Charged = true; changed = money = true;
                                    try { e.ChargedDay = SaveGameManager.Current?.Day ?? 0; } catch { }
                                    MPHub.HostBuildingOfferOutcome(OfferOf(e), true);
                                }
                                break;
                            case "releasefail":   // F4: the seller's own game no longer holds the building
                                if (e.Released || e.DeedMoved || e.Cancelled) return;
                                HostCancel(e, "the seller's own game no longer holds the building");
                                return;
                            case "refunded":
                                if (!e.Refunded) { e.Refunded = true; changed = money = true; }
                                break;
                            case "released":
                                if (e.Cancelled) { Plugin.Logger.LogWarning($"[BldSale] sale {e.SaleId} '{e.AddressKey}': a release arrived after the cancel - not recorded; the deed stays with the seller."); return; }
                                if (!e.Released) { e.Released = true; e.SellerStayed = p.Tenant; changed = true; }
                                if (!e.DeedMoved) { HostMoveDeed(e); changed = true; }
                                break;
                            case "finalized":
                                if (!e.Applied) { e.Applied = true; changed = true; }
                                break;
                            case "paid":
                                if (!e.Paid) { e.Paid = true; changed = money = true; }
                                break;
                        }
                        if (!changed) return;
                        if (money)
                        {
                            // fold 2 (F2): the post-leg figure goes into the store this party's money is restored from, in
                            // the SAME manifest write as the ledger mark below; *Stamped records that it did
                            bool hostSelf = senderPid == MPConfig.PlayerId;
                            if (hostSelf && p.HasMoney) MPServer.RecordCash(MPConfig.PlayerId, p.Money);   // a guest's was recorded on receipt (MPServer)
                            bool stamped = MPSaveCoordinator.StampSlotCash(hostSelf ? MPConfig.StableId : MPServer.StableOf(senderPid));
                            if (p.Action == "charged") e.ChargeStamped = stamped;
                            else if (p.Action == "paid") e.PayStamped = stamped;
                            else e.RefundStamped = stamped;
                        }
                        if (IsDone(e)) { e.Retired = true; lock (_lock) _sales.RemoveAll(x => x.Retired && !x.SellerStayed); }   // a refunded cancel whose figure is stamped needs no save
                        MPHub.SaveLedger();
                        _nextSendAt = 0f;
                        Plugin.Logger.LogInfo($"[BldSale] sale {e.SaleId} '{e.AddressKey}': {p.Action} CONFIRMED by {(seller ? "seller" : "buyer")} {senderPid} ({Legs(e)}){(e.Retired ? " - RETIRED" : e.Charged && e.Released && e.DeedMoved && e.Applied && e.Paid ? " - every leg confirmed; retired once both saves cover it" : "")}.");
                        return;
                    }
                    case "rentpaid": HostRecordRent(p, senderPid); return;
                    case "rentok":
                    {
                        MpRentForwardEntry? r;
                        lock (_lock)
                        {
                            r = _rents.Find(x => x.RentId == p.RentId && ResolvePid(x.PayeePid, x.PayeeStable) == senderPid);
                            if (r != null) { _rents.Remove(r); r.Saved = false; _rentDone.Add(r); }
                        }
                        if (r == null) return;
                        if (senderPid != MPConfig.PlayerId) MPSaveCoordinator.StampSlotCash(MPServer.StableOf(senderPid));
                        MPHub.SaveLedger();
                        Plugin.Logger.LogInfo($"[BldSale] rent {p.RentId}: booked by the deed holder {senderPid} - done.");
                        return;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BldSale] host message '{p.Action}': {ex.Message}"); }
        }

        private static LoanOfferPayload OfferOf(MpBuildingSaleEntry e) => new LoanOfferPayload
        {
            Id = e.SaleId, From = ResolvePid(e.BuyerPid, e.BuyerStable), To = ResolvePid(e.SellerPid, e.SellerStable),
            Principal = e.Amount, Kind = Kind, AddressKey = e.AddressKey, BusinessName = e.Name,
        };

        private static void HostRecordRent(BuildingSalePayload p, string payerPid)
        {
            string addr = p.AddressKey ?? "";
            if (string.IsNullOrEmpty(p.RentId) || addr.Length == 0 || !MPServer.IsSaneMoney(p.Amount) || p.Amount <= 0f) return;
            var ack = new BuildingSalePayload { Action = "rentnoted", RentId = p.RentId, AddressKey = addr };
            bool known;
            lock (_lock) known = _rents.Exists(r => r.RentId == p.RentId) || _rentDone.Exists(r => r.RentId == p.RentId);
            if (!known)
            {
                MPServer.BuildingRealEstateOwners.TryGetValue(addr, out var d);
                d ??= "";
                string payee = d == "host" ? MPConfig.PlayerId : d;
                if (!MPRestSync.AllPlayers().Contains(payee)) { string p2 = ""; try { p2 = MPServer.PidOf(d); } catch { } if (!string.IsNullOrEmpty(p2)) payee = p2; }
                string pst = MPServer.StableOf(payee);
                var entry = new MpRentForwardEntry { RentId = p.RentId, AddressKey = addr, PayerPid = payerPid, PayeePid = payee, PayeeStable = string.IsNullOrEmpty(pst) ? d : pst, Amount = p.Amount, Day = p.Day, Saved = true };
                string why = d.Length == 0 || LedgerNames(d, payerPid, MPServer.StableOf(payerPid)) ? $"'{addr}' has no other player as deed holder ('{d}')"
                           : !RentGoesToDeedHolder(addr, payerPid, d) ? $"{payerPid}'s rent for '{addr}' does not go to the deed holder {payee} (RentGoesToDeedHolder)"
                           : "";
                lock (_lock) { if (why.Length > 0) _rentDone.Add(entry); else { entry.Saved = false; _rents.Add(entry); } }
                MPHub.SaveLedger();
                if (why.Length > 0) Plugin.Logger.LogInfo($"[BldSale] rent {p.RentId}: {why} - recorded, nothing to forward.");
                else
                {
                    _nextSendAt = 0f;
                    Plugin.Logger.LogInfo($"[BldSale] rent {p.RentId}: {payerPid} paid {p.Amount:F2} for '{addr}' (day {p.Day}) - held for the deed holder {payee}.");
                }
            }
            if (payerPid == MPConfig.PlayerId) _rentNoted.Add(p.RentId);
            else MPServer.SendHubTo(payerPid, MessageType.BuildingSale, ack);
        }

        // ── the parties (any machine, MAIN THREAD) ───────────────────────────
        /// <summary>A leg from the host (or the host's own leg, called directly). Not settled yet, or a restored balance
        /// not yet applied (it would overwrite a money leg) = ignored: the host re-sends every 5 s.</summary>
        internal static void OnMessage(BuildingSalePayload p)
        {
            if (p == null) return;
            try
            {
                if (p.Action == "rentnoted") { _rentNoted.Add(p.RentId ?? ""); return; }
                var gi = SaveGameManager.Current;
                if (gi == null || !MPWorldReady.IsSettled)
                {
                    if (!_unsettledLogged) { _unsettledLogged = true; Plugin.Logger.LogInfo($"[BldSale] '{p.Action}' for '{p.AddressKey}' ignored - this world is not settled yet; the host re-sends."); }
                    return;
                }
                _unsettledLogged = false;
                bool moneyLeg = p.Action == "charge" || p.Action == "pay" || p.Action == "rent" || p.Action == "refund";
                if (moneyLeg && MPSaveCoordinator.CashOverlayPending)
                {
                    if (!_overlayLogged) { _overlayLogged = true; Plugin.Logger.LogInfo($"[BldSale] '{p.Action}' for '{p.AddressKey}' waits - the host's balance for this game is not applied yet; the host re-sends."); }
                    return;
                }
                _overlayLogged = false;
                if (DevHold.Length > 0 && (DevHold == p.Action || DevHold == "all"))
                { Plugin.Logger.LogInfo($"[BldSale] DEV hold: {p.Action} for sale {p.SaleId} ('{p.AddressKey}') NOT applied, not confirmed."); return; }
                string reply; bool tenant = false;
                switch (p.Action)
                {
                    case "charge":   reply = BuyerCharge(p); break;
                    case "release":  reply = SellerRelease(p, out tenant); if (reply.Length == 0) return; break;
                    case "refund":   if (!BuyerRefund(p)) return; reply = "refunded"; break;
                    case "finalize": if (!BuyerFinalize(p)) return; reply = "finalized"; break;
                    case "pay":      if (!SellerPaid(p)) return; reply = "paid"; break;
                    case "rent":     if (!PayeeApply(p)) return; reply = "rentok"; break;
                    default: return;
                }
                if (DevDropOnce.Length > 0 && DevDropOnce == reply)
                {
                    DevDropOnce = "";
                    Plugin.Logger.LogInfo($"[BldSale] DEV drop: the '{reply}' confirmation for sale {p.SaleId} ('{p.AddressKey}') was applied but NOT sent (lost once).");
                    return;
                }
                var r = new BuildingSalePayload { Action = reply, SaleId = p.SaleId, RentId = p.RentId, AddressKey = p.AddressKey, Tenant = tenant };
                if (moneyLeg) { r.Money = gi.Money; r.HasMoney = true; }
                if (MPServer.IsRunning) HostOnMessage(r, MPConfig.PlayerId);
                else MPClient.SendHub(MessageType.BuildingSale, r);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BldSale] leg '{p.Action}' for '{p.AddressKey}': {ex.Message}"); }
        }

        /// <summary>BUYER: the escrow - the game's own charge of its own buy (BizManPresentation.cs:906-910 refuses below
        /// the offer with Notifications.ShowInsufficientMoney; :925-933 books ba:transaction_buildingbought, tax-deductible
        /// as the game decides). Committed = the seller already gave the building up (only after a restart rolled the
        /// host's own charge back): then it is taken even below zero, the deal being done.</summary>
        private static string BuyerCharge(BuildingSalePayload p)
        {
            var gi = SaveGameManager.Current!;
            string tag = "sale|" + p.SaleId + "|charge";
            if (MoneyAlreadyHere(tag)) { Plugin.Logger.LogInfo($"[BldSale] charge for sale {p.SaleId}: already taken by this game - confirmed, no money moved."); return "charged"; }
            if (!p.Committed && gi.Money < p.Amount)
            {
                try { UI.Notification.Notifications.ShowInsufficientMoney(); } catch { }
                Plugin.Logger.LogInfo($"[BldSale] shown: the game's own insufficient-money notice (charge for sale {p.SaleId}: {p.Amount:F2} > balance {gi.Money:F2}; nothing moved).");
                return "chargefail";
            }
            var reg = GameStatePatcher.FindRegistration(p.AddressKey ?? "");
            string text = reg != null ? Fmt(reg, p.AddressKey ?? "") : (p.Name ?? p.AddressKey ?? "");
            var ti = new TransactionInfo("ba:transaction_buildingbought", new Dictionary<string, string> { { "address", text }, { TagKey, tag } });
            if (reg != null && BusinessHelper.IsTaxDeductibleBusinessBuilding(reg)) ti.SetTaxDeductibleName(text);   // :928-931
            GameManager.ChangeMoneySafe(-p.Amount, ti, null, reg?.Address, force: p.Committed);
            _moneyHere.Add(tag);
            try { SaveGameManager.MarkChange(); } catch { }
            Plugin.Logger.LogInfo($"[BldSale] buyer charge APPLIED for sale {p.SaleId}: '{p.AddressKey}' -{p.Amount:F2} as ba:transaction_buildingbought (escrow on the host's ledger).");
            return "charged";
        }

        /// <summary>SELLER: the game's own computer-buy removal (RealEstateHelper.cs:136-149) WITHOUT the payment (the "pay"
        /// leg brings it), its own listing cancelled here (fold F4). Building only: replayed until this game's save covers
        /// it, so it follows this game's own list and never moves money. Fold 2: a FIRST release (not Committed) is applied
        /// only while the release window is open (before the day before the host's deadline) and only while this game
        /// still holds the building (F4) - else "releasefail" and the host cancels. Returns the reply, "" = not now.</summary>
        private static string SellerRelease(BuildingSalePayload p, out bool tenant)
        {
            tenant = false;
            var gi = SaveGameManager.Current;
            var reg = GameStatePatcher.FindRegistration(p.AddressKey ?? "");
            if (gi == null || reg == null) { Plugin.Logger.LogWarning($"[BldSale] release for sale {p.SaleId}: no registration for '{p.AddressKey}' yet."); return ""; }
            var a = reg.Address;
            if (!p.Committed && p.Deadline > 0 && gi.Day >= p.Deadline - 1)
            {
                if (_windowLogged.Add(p.SaleId ?? "")) Plugin.Logger.LogInfo($"[BldSale] release for sale {p.SaleId}: the release window is closed (day {gi.Day}, the host cancels from day {p.Deadline}) - not applied, not confirmed.");
                return "";
            }
            try { tenant = reg.RentedByPlayer; } catch { }
            bool inList = gi.realEstate.Exists(x => x != null && x.address == a);
            int listings = 0; try { listings = gi.buildingsForSale.RemoveAll(x => x != null && x.address == a); } catch { }
            if (!inList)
            {
                string stamp = ""; try { stamp = reg.buildingOwnerRivalId ?? ""; } catch { }
                if (!p.Committed && stamp != p.BuyerId)
                {
                    Plugin.Logger.LogWarning($"[BldSale] release for sale {p.SaleId}: '{p.AddressKey}' is not in this game's real estate and this sale did not give it up (stamp '{stamp}') - this game no longer holds it; refused, the host cancels the sale.");
                    return "releasefail";
                }
                if (listings > 0) { RefreshPoi(reg); try { SaveGameManager.MarkChange(); } catch { } }
                Plugin.Logger.LogInfo($"[BldSale] release for sale {p.SaleId}: '{p.AddressKey}' is already not in this game's real estate (inList=False) - confirmed, nothing applied; {listings} own listing(s) cancelled.");
                return "released";
            }
            reg.buildingOwnerRivalId = p.BuyerId;          // another player's deed (the session's deed stamp, Protocol.cs:2833-2834)
            gi.realEstate.RemoveAll(x => x != null && x.address == a);
            if (tenant) reg.RentPerDay = reg.BuildingCached.GetBuildingDailyMarketRent();   // RealEstateHelper.cs:143-146
            RefreshPoi(reg);
            try { RealEstateHelper.AddNoHomeModifierIfNeeded(); } catch { }
            try { SaveGameManager.MarkChange(); } catch { }
            Plugin.Logger.LogInfo($"[BldSale] seller release APPLIED for sale {p.SaleId}: '{p.AddressKey}' given up to {p.BuyerId} (building only, inList=True); {listings} own listing(s) cancelled; tenant={tenant} rent/day={reg.RentPerDay:F2}.");
            return "released";
        }

        /// <summary>BUYER: the game's own buy (BizManPresentation.cs:925-962) WITHOUT the charge (the "charge" leg took
        /// it), the seller's occupancy and rent price in place of the random ones. Building only, like SellerRelease.</summary>
        private static bool BuyerFinalize(BuildingSalePayload p)
        {
            var gi = SaveGameManager.Current;
            var reg = GameStatePatcher.FindRegistration(p.AddressKey ?? "");
            if (gi == null || reg == null) { Plugin.Logger.LogWarning($"[BldSale] finalize for sale {p.SaleId}: no registration for '{p.AddressKey}' yet."); return false; }
            var a = reg.Address;
            if (gi.realEstate.Exists(x => x != null && x.address == a))
            {
                Plugin.Logger.LogInfo($"[BldSale] finalize for sale {p.SaleId}: '{p.AddressKey}' is already in this game's real estate (inList=True) - confirmed, nothing applied.");
                return true;
            }
            int sqm = 0; try { sqm = BuildingHelper.GetBuilding(a).totalSqm; } catch { }
            float ppsqm = p.PricePerSqm;
            if (ppsqm <= 0f) { try { ppsqm = reg.BuildingCached.GetBuildingDailyMarketRentPerSqm(); } catch { } }
            gi.realEstate.Add(new RealEstate
            {
                address = a, purchasePrice = p.Amount, purchaseDay = gi.Day, totalSqm = sqm,
                occupancy = p.Occupancy, pricePerSqm = ppsqm,
            });
            gi.buildingsForSale.RemoveAll(x => x != null && x.address == a);
            bool tenant = false; try { tenant = reg.RentedByPlayer; } catch { }
            if (tenant) reg.RentPerDay = 0f;
            reg.buildingOwnerRivalId = string.Empty;
            RefreshPoi(reg);
            try { GameEvent.Invoke("ba:gameevent_purchasedbuilding"); } catch { }
            try { SaveGameManager.MarkChange(); } catch { }
            try { PassengerHud.Toast($"'{p.Name}' is yours — {p.SellerId} accepted your ${p.Amount:N0} offer.", 6f); } catch { }
            Plugin.Logger.LogInfo($"[BldSale] buyer finalize APPLIED for sale {p.SaleId}: '{p.AddressKey}' from {p.SellerId} (building only, inList=False); occupancy {p.Occupancy:F1}, rent/m2 {ppsqm:F2}.");
            return true;
        }

        /// <summary>SELLER: paid from escrow, as the game's own computer-buy payment (RealEstateHelper.cs:139: neither day
        /// nor address passed) booked as ba:transaction_buildingsold.</summary>
        private static bool SellerPaid(BuildingSalePayload p)
        {
            string tag = "sale|" + p.SaleId + "|pay";
            if (MoneyAlreadyHere(tag)) { Plugin.Logger.LogInfo($"[BldSale] pay for sale {p.SaleId}: already booked by this game - confirmed, no money moved."); return true; }
            var reg = GameStatePatcher.FindRegistration(p.AddressKey ?? "");
            string text = reg != null ? Fmt(reg, p.AddressKey ?? "") : (p.Name ?? p.AddressKey ?? "");
            var ti = new TransactionInfo("ba:transaction_buildingsold", new Dictionary<string, string> { { "address", text }, { TagKey, tag } });
            GameManager.ChangeMoneySafe(p.Amount, ti);
            _moneyHere.Add(tag);
            try { SaveGameManager.MarkChange(); } catch { }
            try { PassengerHud.Toast($"Sold '{p.Name}' to {p.BuyerId} for ${p.Amount:N0}.", 6f); } catch { }
            Plugin.Logger.LogInfo($"[BldSale] seller pay APPLIED for sale {p.SaleId}: '{p.AddressKey}' +{p.Amount:F2} as ba:transaction_buildingsold.");
            return true;
        }

        /// <summary>BUYER, fold 2 (c): the escrow comes back once - the reverse of its own charge line (the same game
        /// transaction type, ba:transaction_buildingbought, with the amount returned; the game has no building-refund type).
        /// Committed = the charge is certainly taken (a guest's, or a stamped one); otherwise (the host's own, unstamped,
        /// its money back from its own save) the refund moves money only when that save holds the charge.</summary>
        private static bool BuyerRefund(BuildingSalePayload p)
        {
            string tag = "sale|" + p.SaleId + "|refund", charge = "sale|" + p.SaleId + "|charge";
            if (MoneyAlreadyHere(tag)) { Plugin.Logger.LogInfo($"[BldSale] refund for sale {p.SaleId}: already returned by this game - confirmed, no money moved."); return true; }
            if (!p.Committed && !MoneyAlreadyHere(charge)) { Plugin.Logger.LogInfo($"[BldSale] refund for sale {p.SaleId}: this game's own save never took the charge - nothing to return; confirmed."); return true; }
            var reg = GameStatePatcher.FindRegistration(p.AddressKey ?? "");
            string text = reg != null ? Fmt(reg, p.AddressKey ?? "") : (p.Name ?? p.AddressKey ?? "");
            var ti = new TransactionInfo("ba:transaction_buildingbought", new Dictionary<string, string> { { "address", text }, { TagKey, tag } });
            GameManager.ChangeMoneySafe(p.Amount, ti, null, reg?.Address);
            _moneyHere.Add(tag);
            try { SaveGameManager.MarkChange(); } catch { }
            Plugin.Logger.LogInfo($"[BldSale] buyer refund APPLIED for sale {p.SaleId}: '{p.AddressKey}' +{p.Amount:F2} (the escrow returned; the seller never released).");
            ShowSaleRefunded(p, text);
            return true;
        }

        /// <summary>Fold 2 (c): the buyer's notice that a sale was cancelled and the escrow returned (decision 53).</summary>
        internal static void ShowSaleRefunded(BuildingSalePayload p, string address)
        {
            string seller = string.IsNullOrEmpty(p.SellerName) ? (p.SellerId ?? "") : p.SellerName;
            Plugin.Logger.LogInfo($"[BldSale] refund notice for sale {p.SaleId} ('{p.AddressKey}', {p.Amount:F2}).");
            ShowNote(UI.Notification.NotificationType.Info, RefundedText(seller, address));
        }

        /// <summary>DEED HOLDER: book one day's rent a player tenant paid, once (the host sends it only while unrecorded).</summary>
        private static bool PayeeApply(BuildingSalePayload p)
        {
            string tag = p.RentId ?? "";
            if (MoneyAlreadyHere(tag)) { Plugin.Logger.LogInfo($"[BldSale] rent {p.RentId}: already booked by this game - confirmed."); return true; }
            var reg = GameStatePatcher.FindRegistration(p.AddressKey ?? "");
            string text = reg != null ? Fmt(reg, p.AddressKey ?? "") : (p.AddressKey ?? "");
            var ti = new TransactionInfo("ba:transaction_rentrevenue", new Dictionary<string, string> { { "address", text }, { TagKey, tag } });
            GameManager.ChangeMoneySafe(p.Amount, ti, p.Day, reg?.Address, force: true);   // RealEstateHelper.cs:80-81
            _moneyHere.Add(tag);
            try { SaveGameManager.MarkChange(); } catch { }
            Plugin.Logger.LogInfo($"[BldSale] rent {p.RentId}: +{p.Amount:F2} from {p.SellerId} for '{p.AddressKey}' (day {p.Day}) booked as ba:transaction_rentrevenue.");
            return true;
        }

        /// <summary>PAYER (any machine, after the game's own RunDaily): every native rent charge of the last 3 days for a
        /// building ANOTHER player holds the deed of goes to the host until the host has recorded it; the host decides
        /// (RentGoesToDeedHolder) and remembers the id.</summary>
        internal static void OnRunDaily()
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected) return;
            var gi = SaveGameManager.Current;
            if (gi?.Transactions == null) return;
            int today = gi.Day;
            var send = new List<BuildingSalePayload>();
            foreach (var t in gi.Transactions)
            {
                if (t == null || t.transactionType != "ba:transaction_rent" || t.amount >= 0f || t.address == null) continue;
                if (t.timestamp.Day < today - 2) continue;
                string key = GameStateReader.AddressKey(t.address);
                var reg = GameStatePatcher.FindRegistration(key);
                if (reg == null || MergerFlip.IsFlipped(key) || !IsOtherPlayersDeed(reg)) continue;
                string id = $"rent|{key}|{t.timestamp.Day}|{MPConfig.PlayerId}";
                if (_rentNoted.Contains(id)) continue;
                send.Add(new BuildingSalePayload { Action = "rentpaid", RentId = id, AddressKey = key, Amount = -t.amount, Day = t.timestamp.Day });
            }
            foreach (var p in send)
            {
                Plugin.Logger.LogInfo($"[BldSale] rent {p.RentId}: this game paid {p.Amount:F2} for '{p.AddressKey}' to another player's building - reported to the host.");
                if (MPServer.IsRunning) HostRecordRent(p, MPConfig.PlayerId);
                else MPClient.SendHub(MessageType.BuildingSale, p);
            }
        }

        // ── manifest ─────────────────────────────────────────────────────────
        private static MpBuildingSaleEntry Copy(MpBuildingSaleEntry e) => new MpBuildingSaleEntry
        {
            SaleId = e.SaleId, AddressKey = e.AddressKey, Name = e.Name, BuyerPid = e.BuyerPid, BuyerStable = e.BuyerStable,
            SellerPid = e.SellerPid, SellerStable = e.SellerStable, Amount = e.Amount, Occupancy = e.Occupancy,
            PricePerSqm = e.PricePerSqm, Day = e.Day, Charged = e.Charged, Released = e.Released, DeedMoved = e.DeedMoved,
            Applied = e.Applied, Paid = e.Paid, BuyerSaved = e.BuyerSaved, SellerSaved = e.SellerSaved,
            SellerStayed = e.SellerStayed, Retired = e.Retired,
            ChargeStamped = e.ChargeStamped, PayStamped = e.PayStamped, ChargedDay = e.ChargedDay,
            Cancelled = e.Cancelled, Refunded = e.Refunded, RefundStamped = e.RefundStamped,
        };

        private static MpRentForwardEntry Copy(MpRentForwardEntry r) => new MpRentForwardEntry
        {
            RentId = r.RentId, AddressKey = r.AddressKey, PayerPid = r.PayerPid, PayeePid = r.PayeePid,
            PayeeStable = r.PayeeStable, Amount = r.Amount, Day = r.Day, Saved = r.Saved,
        };

        internal static List<MpBuildingSaleEntry> SnapshotSales() { lock (_lock) return _sales.Select(Copy).ToList(); }
        internal static List<MpRentForwardEntry> SnapshotRents() { lock (_lock) return _rents.Select(Copy).ToList(); }
        internal static List<MpRentForwardEntry> SnapshotRentDone() { lock (_lock) return _rentDone.Select(Copy).ToList(); }

        /// <summary>HOST: REPLACE the records from the manifest being restored (after the deed ledger). The host's OWN
        /// legs its own save does not cover are pending again (fold F2: its money and buildings came back from that save;
        /// its own tagged transaction lines decide "already taken?"), and so is a rent it booked unsaved. A sale whose
        /// deed was moved but whose deed ledger names the seller again (the ledger came from an older write) names the
        /// buyer again.</summary>
        internal static void Restore(MpManifest? m)
        {
            int s = 0, r = 0, reopened = 0, fixedLedger = 0;
            lock (_lock)
            {
                _sales.Clear(); _rents.Clear(); _rentDone.Clear(); _confirmGen.Clear();
                foreach (var e in m?.BuildingSales ?? new List<MpBuildingSaleEntry>())
                {
                    if (e == null || string.IsNullOrEmpty(e.SaleId) || string.IsNullOrEmpty(e.AddressKey)) continue;
                    _sales.Add(e); s++;
                    if (e.Retired) continue;
                    // fold 2 (F2): the host's money legs follow the store its money comes back from - a STAMPED leg rode
                    // the slot/wallet write and is never re-run; an unstamped one (no slot carried it) follows its own
                    // save's tagged line. Its BUILDING legs always follow its own save (idempotent on the game's list).
                    if (IsHostRole(e.BuyerPid, e.BuyerStable) && !e.BuyerSaved)
                    {
                        if (e.Applied) { e.Applied = false; reopened++; }
                        if (e.Charged && !e.ChargeStamped && !e.Cancelled) { e.Charged = false; reopened++; }
                        if (e.Refunded && !e.RefundStamped) { e.Refunded = false; reopened++; }
                    }
                    if (IsHostRole(e.SellerPid, e.SellerStable) && !e.SellerSaved)
                    {
                        if (e.Released) { e.Released = false; reopened++; }
                        if (e.Paid && !e.PayStamped) { e.Paid = false; reopened++; }
                    }
                    if (!e.DeedMoved) continue;
                    MPServer.BuildingRealEstateOwners.TryGetValue(e.AddressKey, out var d);
                    if (string.IsNullOrEmpty(d) || LedgerNames(d, e.SellerPid, e.SellerStable))
                    {
                        string buyer = e.BuyerStable == MPConfig.StableId ? MPConfig.PlayerId : ResolvePid("", e.BuyerStable);
                        MPServer.BuildingRealEstateOwners[e.AddressKey] = string.IsNullOrEmpty(buyer) ? (string.IsNullOrEmpty(e.BuyerStable) ? e.BuyerPid : e.BuyerStable) : buyer;
                        fixedLedger++;
                    }
                }
                foreach (var x in m?.RentForwards ?? new List<MpRentForwardEntry>())
                    if (x != null && !string.IsNullOrEmpty(x.RentId)) { _rents.Add(x); r++; }
                foreach (var x in m?.RentDone ?? new List<MpRentForwardEntry>())
                {
                    if (x == null || string.IsNullOrEmpty(x.RentId)) continue;
                    if (!x.Saved && IsHostRole(x.PayeePid, x.PayeeStable)) { _rents.Add(x); reopened++; }
                    else _rentDone.Add(x);
                }
            }
            if (s + r + reopened > 0) Plugin.Logger.LogInfo($"[BldSale] restored {s} sale(s) ({fixedLedger} deed entr(ies) re-pointed to the buyer, {reopened} of the host's own unsaved leg(s)/rent(s) pending again) and {r} held rent(s) from the manifest.");
        }

#if BAMP_DEV
        // ── DEV levers (TestDrive.cs) ─────────────────────────────────────────
        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>bldoffer &lt;addr&gt; &lt;amount&gt;: press the game's own buy box on &lt;addr&gt; with that offer.</summary>
        internal static string DevBldOffer(string arg)
        {
            var parts = (arg ?? "").Trim().Split(' ');
            if (parts.Length < 2 || !double.TryParse(parts[parts.Length - 1], System.Globalization.NumberStyles.Float, Inv, out var amt) || amt <= 0)
                return "ERR usage: bldoffer <addr> <amount>";
            string addr = string.Join(" ", parts, 0, parts.Length - 1).Trim();
            var gi = SaveGameManager.Current;
            if (gi == null) return "ERR no game instance";
            var reg = GameStatePatcher.FindRegistration(addr);
            if (reg == null) return $"ERR no registration for '{addr}'";
            string key = GameStateReader.AddressKey(reg);
            var uis = InstanceBehavior<UI.UIs>.Instance;
            if (uis == null || uis.fullMenu == null || uis.fullMenu.bizMan == null) return "ERR bldoffer: no BizMan app";
            uis.fullMenu.bizMan.Open(reg.Address);
            BizManPresentation? pres = null;
            foreach (var x in UnityEngine.Object.FindObjectsOfType<BizManPresentation>(true))
                if (MPPatches.Patch_SendBuyBuildingOffer.PageAddress(x) == key) { pres = x; break; }
            if (pres == null) return $"ERR bldoffer: the BizMan page did not open on '{key}'";
            if (!MPPatches.Patch_SendBuyBuildingOffer.SetOfferText(pres, Math.Ceiling(amt).ToString("F0", System.Globalization.CultureInfo.CurrentCulture)))
                return "ERR bldoffer: no offer field on the page";
            double before = gi.Money;
            pres.SendBuyBuildingOffer();
            int outN = MPHub.OutgoingOffers.Count(o => o.Kind == Kind && o.AddressKey == key);
            return $"OK bldoffer addr='{key}' amount={Math.Ceiling(amt).ToString("F2", Inv)} moneyBefore={before.ToString("F2", Inv)} moneyNow={((double)gi.Money).ToString("F2", Inv)} held={MPPatches.Patch_SendBuyBuildingOffer.HasPendingClaim} otherDeed={IsOtherPlayersDeed(reg)} outgoing={outN}";
        }

        /// <summary>hubanswer &lt;id|last&gt; accept|decline: answer one of MY incoming hub offers (the Business app's buttons).</summary>
        internal static string DevHubAnswer(string arg)
        {
            var parts = (arg ?? "").Trim().Split(' ');
            if (parts.Length != 2 || (parts[1] != "accept" && parts[1] != "decline")) return "ERR usage: hubanswer <id|last> accept|decline";
            var o = parts[0] == "last" ? MPHub.IncomingOffers.LastOrDefault() : MPHub.IncomingOffers.Find(x => x.Id == parts[0]);
            if (o == null) return $"ERR hubanswer: no incoming offer '{parts[0]}' (incoming={MPHub.IncomingOffers.Count})";
            MPHub.AnswerOffer(o.Id, parts[1] == "accept");
            return $"OK hubanswer id={o.Id} kind={o.Kind} addr='{o.AddressKey}' from={o.From} amount={o.Principal.ToString("F2", Inv)} accept={parts[1] == "accept"}";
        }

        /// <summary>deedstate &lt;addr&gt;: this game's own deed facts for one building (+ the host's ledger).</summary>
        internal static string DevDeedState(string arg)
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return "ERR no game instance";
            var reg = GameStatePatcher.FindRegistration((arg ?? "").Trim());
            if (reg == null) return $"ERR no registration for '{arg}'";
            string key = GameStateReader.AddressKey(reg);
            var re = gi.realEstate.Find(x => x != null && x.address == reg.Address);
            bool listed = gi.buildingsForSale.Exists(x => x != null && x.address == reg.Address);
            bool owned = false; try { owned = reg.BuildingOwnedByPlayer; } catch { }
            string deed = MPServer.IsRunning ? (MPServer.BuildingRealEstateOwners.TryGetValue(key, out var d) ? d : "") : "n/a";
            return $"OK deedstate addr='{key}' owned={owned} record={(re != null)} occupancy={(re?.occupancy ?? -1f).ToString("F1", Inv)} ppsqm={(re?.pricePerSqm ?? -1f).ToString("F2", Inv)} listed={listed} stamp={reg.buildingOwnerRivalId} rented={reg.RentedByPlayer} rentPerDay={reg.RentPerDay.ToString("F2", Inv)} money={((double)gi.Money).ToString("F2", Inv)} deed={deed}";
        }

        /// <summary>xferstate: the host's sale/rent records and this machine's building offers.</summary>
        internal static string DevXferState()
        {
            var sb = new StringBuilder("OK xferstate sales=[");
            lock (_lock)
            {
                sb.Append(string.Join(";", _sales.Select(e => $"{e.SaleId}:{e.AddressKey}:{e.SellerPid}->{e.BuyerPid}:{e.Amount.ToString("F2", Inv)}:charged={e.Charged}:released={e.Released}:deed={e.DeedMoved}:applied={e.Applied}:paid={e.Paid}:buyerSaved={e.BuyerSaved}:sellerSaved={e.SellerSaved}:stayed={e.SellerStayed}:retired={e.Retired}:cancelled={e.Cancelled}:refunded={e.Refunded}:stamped={e.ChargeStamped}/{e.PayStamped}/{e.RefundStamped}")));
                sb.Append("] rents=[").Append(string.Join(";", _rents.Select(r => $"{r.RentId}:{r.Amount.ToString("F2", Inv)}->{r.PayeePid}")));
                sb.Append($"] rentIds={_rentDone.Count} saveRound={_saveGen}");
            }
            sb.Append(" in=[").Append(string.Join(";", MPHub.IncomingOffers.Where(o => o.Kind == Kind).Select(o => $"{o.Id}:{o.AddressKey}:{o.From}:{o.Principal.ToString("F2", Inv)}")));
            sb.Append("] out=[").Append(string.Join(";", MPHub.OutgoingOffers.Where(o => o.Kind == Kind).Select(o => $"{o.Id}:{o.AddressKey}:{o.To}:{o.Principal.ToString("F2", Inv)}")));
            sb.Append($"] hold={(DevHold.Length > 0 ? DevHold : "off")} rentNoted={_rentNoted.Count} drop={(DevDropOnce.Length > 0 ? DevDropOnce : "off")} refundDays={RefundDays}");
            return sb.ToString();
        }

        /// <summary>txlines &lt;addr&gt; &lt;type&gt;: this game's transactions of one type for one building (address field or
        /// the address in the data); tagged = booked by T-50 (sale/rent id in the data), native = the game's own.</summary>
        internal static string DevTxLines(string arg)
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return "ERR no game instance";
            var parts = (arg ?? "").Trim().Split(' ');
            if (parts.Length < 2) return "ERR usage: txlines <addr> <transactionType>";
            string type = parts[parts.Length - 1];
            string addr = string.Join(" ", parts, 0, parts.Length - 1).Trim();
            var reg = GameStatePatcher.FindRegistration(addr);
            if (reg == null) return $"ERR no registration for '{addr}'";
            string key = GameStateReader.AddressKey(reg), text = Fmt(reg, key);
            int tagged = 0, native = 0; double tsum = 0, nsum = 0; var ids = new List<string>();
            foreach (var t in gi.Transactions)
            {
                if (t == null || t.transactionType != type) continue;
                bool here = false;
                try { here = (t.address != null && GameStateReader.AddressKey(t.address) == key) || (t.transactionData != null && t.transactionData.TryGetValue("address", out var ta) && ta == text); } catch { }
                if (!here) continue;
                if (t.transactionData != null && t.transactionData.TryGetValue(TagKey, out var tg)) { tagged++; tsum += t.amount; ids.Add(tg); }
                else { native++; nsum += t.amount; }
            }
            return $"OK txlines addr='{key}' type={type} tagged={tagged} taggedsum={tsum.ToString("F2", Inv)} native={native} nativesum={nsum.ToString("F2", Inv)} ids=[{string.Join(",", ids)}]";
        }

        /// <summary>bldrent &lt;addr&gt;: this player starts renting a building it owns, through the game's own RentBuilding
        /// (the call the game's own Hamptons buy makes, BizManPresentation.cs:960) - t-buildingsale T3's tenant premise.</summary>
        internal static string DevBldRent(string arg)
        {
            var reg = GameStatePatcher.FindRegistration((arg ?? "").Trim());
            if (reg == null) return $"ERR no registration for '{arg}'";
            BuildingHelper.RentBuilding(reg.BuildingCached, 0f, 0f);
            return $"OK bldrent addr='{GameStateReader.AddressKey(reg)}' rented={reg.RentedByPlayer} owned={reg.BuildingOwnedByPlayer} rentPerDay={reg.RentPerDay.ToString("F2", Inv)}";
        }

        /// <summary>deedset &lt;addr&gt; &lt;pid|clear&gt;: HOST deed-ledger write (t-buildingsale T6's Hamptons premise).</summary>
        internal static string DevDeedSet(string arg)
        {
            if (!MPServer.IsRunning) return "ERR host only";
            var parts = (arg ?? "").Trim().Split(' ');
            if (parts.Length < 2) return "ERR usage: deedset <addr> <pid|clear>";
            string who = parts[parts.Length - 1], addr = string.Join(" ", parts, 0, parts.Length - 1).Trim();
            if (who == "clear") MPServer.BuildingRealEstateOwners.TryRemove(addr, out _);
            else MPServer.BuildingRealEstateOwners[addr] = who;
            return $"OK deedset addr='{addr}' deed={(MPServer.BuildingRealEstateOwners.TryGetValue(addr, out var d) ? d : "")}";
        }

        /// <summary>moneymark &lt;name&gt;: remember this game's balance under name for this player, across a relaunch
        /// (UnityEngine.PlayerPrefs; both rig instances share it, so the key names the player).</summary>
        internal static string DevMoneyMark(string arg)
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return "ERR no game instance";
            string name = (arg ?? "").Trim();
            if (name.Length == 0) return "ERR usage: moneymark <name>";
            UnityEngine.PlayerPrefs.SetString($"bamp_dev_moneymark_{MPConfig.PlayerId}_{name}", ((double)gi.Money).ToString("R", Inv));
            UnityEngine.PlayerPrefs.Save();
            return $"OK moneymark name={name} money={((double)gi.Money).ToString("F2", Inv)}";
        }

        /// <summary>moneydelta &lt;name&gt;: this game's balance now minus the one moneymark remembered (money exactly once).</summary>
        internal static string DevMoneyDelta(string arg)
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return "ERR no game instance";
            string name = (arg ?? "").Trim();
            string v = UnityEngine.PlayerPrefs.GetString($"bamp_dev_moneymark_{MPConfig.PlayerId}_{name}", "");
            if (!double.TryParse(v, System.Globalization.NumberStyles.Float, Inv, out var before)) return $"ERR moneydelta: no mark '{name}'";
            return $"OK moneydelta name={name} delta={((double)gi.Money - before).ToString("F2", Inv)} money={((double)gi.Money).ToString("F2", Inv)}";
        }

        /// <summary>bldhampton: the first Hamptons house this machine neither owns nor rents.</summary>
        internal static string DevFindHampton()
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return "ERR no game instance";
            foreach (var reg in gi.BuildingRegistrations)
            {
                try
                {
                    if (reg?.BuildingCached == null || !reg.BuildingCached.IsHamptonsHouse() || reg.BuildingOwnedByPlayer || reg.RentedByPlayer) continue;
                    string key = GameStateReader.AddressKey(reg);
                    if (MPServer.IsRunning && MPServer.BuildingRealEstateOwners.TryGetValue(key, out var d) && !string.IsNullOrEmpty(d)) continue;
                    bool dlc = false; try { dlc = global::Extensions.GenericExtensions.DlcIsOwned(reg.BuildingCached.requiredDLC); } catch { }   // the game's own DLC gate (BizManPresentation.cs:891)
                    return $"OK bldhampton addr='{key}' dlc={dlc}";
                }
                catch { }
            }
            return "ERR bldhampton: no free Hamptons house";
        }
#endif
    }

    /// <summary>T-50 decision 37: after the game's own daily pass (BusinessHelper.RunDaily, whose rent loop is :122-146)
    /// report this game's native rent charges for buildings another player holds the deed of.</summary>
    [HarmonyPatch(typeof(Helpers.BusinessHelper), nameof(Helpers.BusinessHelper.RunDaily))]
    public static class Patch_BusinessHelper_RunDaily_BuildingRent
    {
        static void Postfix()
        {
            try { MPBuildingSale.OnRunDaily(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BldSale] day-change rent report: {ex.Message}"); }
        }
    }

#if BAMP_DEV
    /// <summary>DEV (registered probe of T-50's scenario, fold F8): every toast the player is shown, as shown - the hub
    /// notices (decline, accept, enforcement: MPChat.AddNotice) and the sale toasts reach the screen through it.</summary>
    [HarmonyPatch(typeof(PassengerHud), nameof(PassengerHud.Toast))]
    public static class Patch_DevShownToast
    {
        static void Postfix(string msg)
        {
            try { Plugin.Logger.LogInfo($"[DevShown] toast: {msg}"); } catch { }
        }
    }
#endif
}
