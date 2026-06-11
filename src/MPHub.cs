using System;
using System.Collections.Generic;
using Helpers;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Business Hub (wave 1, 2026-06-10): direct money transfers and
    /// player-to-player LOANS in the game's own convention (flat DAILY
    /// interest + daily principal payment — mirrors the bank's Loan model).
    ///
    /// Money model: each machine owns its own wallet (gi.Money — the same
    /// write the save coordinator already uses).  A transfer debits the
    /// sender locally and ships a credit through the host (MoneyAdjust).
    /// The HOST owns the loan ledger: offers route through it, acceptance
    /// moves the principal, and on every game-day rollover it drafts
    /// interest+payment from borrower to lender (negative balances allowed —
    /// the game itself has overdraft).  Ledger is session-scoped (v1).
    /// </summary>
    public static class MPHub
    {
        // ── Shared state (host-broadcast) ─────────────────────────────────────
        public static readonly List<LoanEntry> Loans = new();

        // Incoming offers awaiting MY answer (this machine).
        public static readonly List<LoanOfferPayload> IncomingOffers = new();
        // Offers I sent that are still pending (cancellable).
        public static readonly List<LoanOfferPayload> OutgoingOffers = new();
        public static int PendingCount => IncomingOffers.Count;
        public static int Version;                 // bump = UI refresh + badge

        public static void Reset()
        {
            Loans.Clear();
            IncomingOffers.Clear();
            OutgoingOffers.Clear();
            _hostLoans.Clear();
            _hostOffers.Clear();
            _lastDay = -1;
            _ledgerLoaded = false;   // next session loads its own ledger file
            Version++;
        }

        // ── Wallet ────────────────────────────────────────────────────────────
        public static float MyMoney()
        {
            try { return SaveGameManager.Current?.Money ?? 0f; }
            catch { return 0f; }
        }

        /// <summary>MAIN THREAD: adjust the local wallet.  notice=false keeps the
        /// chat clean (the receiver of an accepted offer already knows; daily
        /// loan drafts live in the Hub list, not the chat).</summary>
        public static void ApplyMoneyDelta(float delta, string reason, bool notice = true)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;
                gi.Money += delta;
                if (notice) MPChat.AddNotice($"{(delta >= 0 ? "+" : "−")}${Mathf.Abs(delta):N0} — {reason}");
                Plugin.Logger.LogInfo($"[Hub] money {(delta >= 0 ? "+" : "")}{delta:N0} ({reason}); balance {gi.Money:N0}.");
                Version++;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Hub] ApplyMoneyDelta: {ex.Message}"); }
        }

        /// <summary>Money not yet promised to pending outgoing offers.</summary>
        public static float AvailableMoney()
        {
            float pending = 0f;
            foreach (var o in OutgoingOffers) pending += o.Principal;
            return MyMoney() - pending;
        }

        private static bool HasPendingTo(string to, string kind)
            => OutgoingOffers.Exists(o => o.To == to && o.Kind == kind);

        // ── Gifts (accept-required: no silent handouts; acceptance = receipt) ─
        public static bool OfferGift(string to, float amount)
        {
            if (amount <= 0 || string.IsNullOrEmpty(to) || to == MPConfig.PlayerId) return false;
            if (HasPendingTo(to, "gift"))
            {
                MPChat.AddNotice($"you already have a pending gift offer to {to}");
                return false;
            }
            if (AvailableMoney() < amount)
            {
                MPChat.AddNotice($"not enough uncommitted money for a ${amount:N0} gift (pending offers count)");
                return false;
            }
            var p = new LoanOfferPayload
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                From = MPConfig.PlayerId, To = to,
                Principal = amount, Kind = "gift",
            };
            OutgoingOffers.Add(p);
            Version++;
            if (MPServer.IsRunning) HostRouteOffer(p);
            else MPClient.SendHub(MessageType.LoanOffer, p);
            return true;
        }

        /// <summary>Cancel one of MY pending offers (the outgoing list's X).</summary>
        public static void CancelOffer(string id)
        {
            var mine = OutgoingOffers.Find(o => o.Id == id);
            if (mine == null) return;
            OutgoingOffers.Remove(mine);
            Version++;
            var p = new LoanOfferPayload { Id = mine.Id, From = mine.From, To = mine.To, Kind = mine.Kind, Principal = mine.Principal, State = "revoke" };
            if (MPServer.IsRunning) HostRouteOffer(p);
            else MPClient.SendHub(MessageType.LoanOffer, p);
        }

        // ── Loans: offer / answer (any role) ──────────────────────────────────
        public static void OfferLoan(string to, float principal, float dailyInterest, float dailyPayment)
        {
            if (principal <= 0 || string.IsNullOrEmpty(to) || to == MPConfig.PlayerId) return;
            if (HasPendingTo(to, "loan"))
            {
                MPChat.AddNotice($"you already have a pending loan offer to {to}");
                return;
            }
            if (AvailableMoney() < principal)
            {
                MPChat.AddNotice($"not enough uncommitted money to offer a ${principal:N0} loan (pending offers count)");
                return;
            }
            var p = new LoanOfferPayload
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                From = MPConfig.PlayerId, To = to,
                Principal = principal, DailyInterest = dailyInterest, DailyPayment = dailyPayment,
            };
            OutgoingOffers.Add(p);
            Version++;
            if (MPServer.IsRunning) HostRouteOffer(p);
            else MPClient.SendHub(MessageType.LoanOffer, p);
        }

        public static void AnswerOffer(string id, bool accept)
        {
            var offer = IncomingOffers.Find(o => o.Id == id);
            if (offer == null) return;
            IncomingOffers.Remove(offer);
            Version++;
            var a = new LoanAnswerPayload { Id = id, From = MPConfig.PlayerId, Accept = accept };
            if (MPServer.IsRunning) HostHandleAnswer(a);
            else MPClient.SendHub(MessageType.LoanAnswer, a);
        }

        // Bank reference (dump-CONFIRMED consts): InterestRate=20 (a TOTAL
        // premium on the principal), YearsToPayLoan=4 → 244-day term (61-day
        // game year); dailies = ceil(P*0.20/244) and ceil(P/244) — matches the
        // user's observed $9 + $41 on $10k exactly.  NO "predatory" indicator
        // by design: what's predatory depends on business ROI, which we can't
        // judge — the borrower decides (user call, 2026-06-10).

        public static int OfferTermDays(LoanOfferPayload o)
            => o.DailyPayment > 0 ? Mathf.Max(1, Mathf.RoundToInt(o.Principal / o.DailyPayment)) : 1;

        public static float OfferTotalPct(LoanOfferPayload o)
            => o.Principal > 0 ? o.DailyInterest * OfferTermDays(o) / o.Principal * 100f : 0f;

        /// <summary>An offer-lifecycle message arrived for ME (main thread).</summary>
        public static void ReceiveOffer(LoanOfferPayload p)
        {
            if (p == null) return;
            // Result for an offer I sent (host-forwarded).
            if (p.State == "accepted" || p.State == "declined")
            {
                if (p.From != MPConfig.PlayerId) return;
                OutgoingOffers.RemoveAll(o => o.Id == p.Id);
                Version++;
                return;
            }
            if (p.To != MPConfig.PlayerId) return;
            if (p.State == "revoke")
            {
                if (IncomingOffers.RemoveAll(o => o.Id == p.Id) > 0)
                    MPChat.AddNotice($"{p.From} withdrew their ${p.Principal:N0} {p.Kind} offer");
                Version++;
                return;
            }
            // No chat notice — the pulsing Business badge is the signal
            // (user call, 2026-06-10).
            IncomingOffers.Add(p);
            Version++;
        }

        public static void ApplyLoanState(LoanStatePayload? p)
        {
            if (p == null) return;
            Loans.Clear();
            Loans.AddRange(p.Loans);
            Plugin.Logger.LogInfo($"[Hub] loan state applied: {Loans.Count} active loan(s).");
            Version++;
        }

        // ── HOST: routing + authoritative ledger ──────────────────────────────
        private static readonly List<LoanEntry> _hostLoans = new();
        private static readonly Dictionary<string, LoanOfferPayload> _hostOffers = new();
        private static int _lastDay = -1;

        public static void HostRouteTransfer(MoneyTransferPayload p)
        {
            if (p == null || p.Amount <= 0) return;
            DeliverMoney(p.To, p.Amount, $"from {p.From}");
        }

        public static void HostRouteOffer(LoanOfferPayload p)
        {
            if (p == null) return;
            if (p.State == "revoke") _hostOffers.Remove(p.Id);
            else _hostOffers[p.Id] = p;
            if (p.To == MPConfig.PlayerId) ReceiveOffer(p);
            else MPServer.SendHubTo(p.To, MessageType.LoanOffer, p);
        }

        public static void HostHandleAnswer(LoanAnswerPayload a)
        {
            if (a == null || !_hostOffers.TryGetValue(a.Id, out var offer)) return;
            string what = offer.Kind == "gift" ? "gift" : "loan";

            // ENFORCEMENT: an offer can't be accepted while the offerer can't
            // cover it (their wallet may have dropped since offering).  The
            // offer stays pending — it re-enables when funds recover.
            if (a.Accept)
            {
                float offererCash = offer.From == MPConfig.PlayerId
                    ? MyMoney()
                    : MPServer.GetKnownCash(offer.From);
                if (offererCash >= 0f && offererCash < offer.Principal)
                {
                    NotifyParty(offer.To, $"{offer.From}'s ${offer.Principal:N0} {what} offer can't be accepted right now — they can't cover it. It stays pending.");
                    NotifyParty(offer.From, $"{offer.To} tried to accept your ${offer.Principal:N0} {what} but your balance can't cover it.");
                    // Put it back in the receiver's incoming list (their accept
                    // click removed it locally).
                    if (offer.To == MPConfig.PlayerId) ReceiveOffer(offer);
                    else MPServer.SendHubTo(offer.To, MessageType.LoanOffer, offer);
                    return;
                }
            }
            _hostOffers.Remove(a.Id);
            // Tell the offerer the outcome (clears their outgoing list).
            var result = new LoanOfferPayload { Id = offer.Id, From = offer.From, To = offer.To, Kind = offer.Kind, Principal = offer.Principal, State = a.Accept ? "accepted" : "declined" };
            if (offer.From == MPConfig.PlayerId) ReceiveOffer(result);
            else MPServer.SendHubTo(offer.From, MessageType.LoanOffer, result);
            if (!a.Accept)
            {
                NotifyParty(offer.From, $"{offer.To} declined your ${offer.Principal:N0} {what} offer.");
                return;
            }
            // Principal moves sender → receiver (overdraft allowed, like the
            // bank).  Only the OFFERER gets a chat line — the receiver just
            // clicked Accept, telling them again is clutter.
            NotifyParty(offer.From, $"{offer.To} accepted your ${offer.Principal:N0} {what}.");
            DeliverMoney(offer.From, -offer.Principal, $"{what} to {offer.To}", silent: true);
            DeliverMoney(offer.To, offer.Principal, $"{what} from {offer.From}", silent: true);
            if (offer.Kind != "gift")
            {
                _hostLoans.Add(new LoanEntry
                {
                    Id = offer.Id, Lender = offer.From, Borrower = offer.To,
                    Remaining = offer.Principal,
                    DailyInterest = offer.DailyInterest, DailyPayment = offer.DailyPayment,
                });
                Plugin.Logger.LogInfo($"[Hub] loan LEDGERED: {offer.From} → {offer.To} ${offer.Principal:N0}; broadcasting ({_hostLoans.Count} active).");
                HostBroadcastLoans();
                SaveLedger();
            }
        }

        /// <summary>PRIVATE notice to one player (gift/loan events are nobody
        /// else''s business): local notice for the host, private chat otherwise.</summary>
        private static void NotifyParty(string playerId, string text)
        {
            if (playerId == MPConfig.PlayerId) MPChat.AddNotice(text);
            else MPServer.SendChatPrivate("Hub", playerId, text);
        }

        /// <summary>HOST tick (main thread): day rollover drafts loan payments.</summary>
        public static void HostTick()
        {
            if (!MPServer.IsRunning) return;
            TryLoadLedger();
            var (d, _) = GameStateReader.GetGameTime();
            if (d == 0) return;
            if (_lastDay < 0) { _lastDay = d; return; }
            if (d == _lastDay) return;
            int days = Math.Max(1, d - _lastDay);   // consensus skips can cross several days
            _lastDay = d;

            bool changed = false;
            for (int i = _hostLoans.Count - 1; i >= 0; i--)
            {
                var ln = _hostLoans[i];
                // FAIRNESS: no accrual unless BOTH parties are in the session —
                // an absent borrower must not rack up interest (and an absent
                // lender's wallet can't be credited anyway).
                var present = MPRestSync.AllPlayers();
                if (!present.Contains(ln.Borrower) || !present.Contains(ln.Lender)) continue;
                for (int k = 0; k < days && ln.Remaining > 0; k++)
                {
                    float pay = Math.Min(ln.DailyPayment, ln.Remaining);
                    float due = ln.DailyInterest + pay;
                    // Silent: daily drafts live in the Hub's loan list, not chat.
                    DeliverMoney(ln.Borrower, -due, $"loan payment to {ln.Lender}", silent: true);
                    DeliverMoney(ln.Lender, due, $"loan payment from {ln.Borrower}", silent: true);
                    ln.Remaining -= pay;
                    changed = true;
                }
                if (ln.Remaining <= 0)
                {
                    NotifyParty(ln.Lender, $"{ln.Borrower} has fully repaid your loan.");
                    NotifyParty(ln.Borrower, $"your loan from {ln.Lender} is fully repaid.");
                    _hostLoans.RemoveAt(i);
                }
            }
            if (changed) { HostBroadcastLoans(); SaveLedger(); }
        }

        // ── Ledger persistence (loans MUST survive sessions) ─────────────────
        private static bool _ledgerLoaded;

        private static string? LedgerPath()
        {
            try
            {
                string session = MPSaveCoordinator.ActiveSessionName;
                if (string.IsNullOrEmpty(session)) return null;
                return System.IO.Path.Combine(MPSaveManager.MpSessionFolder(session), "loans.bamp.json");
            }
            catch { return null; }
        }

        private static bool _warnedNoSession;

        public static void SaveLedger()
        {
            try
            {
                var path = LedgerPath();
                if (path == null)
                {
                    if (!_warnedNoSession)
                    {
                        _warnedNoSession = true;
                        Plugin.Logger.LogWarning("[Hub] ledger NOT saved — no active session name yet (will persist on next change after first save).");
                    }
                    return;
                }
                var st = new LoanStatePayload();
                st.Loans.AddRange(_hostLoans);
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(st));
                Plugin.Logger.LogInfo($"[Hub] ledger saved ({_hostLoans.Count} loan(s)).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Hub] SaveLedger: {ex.Message}"); }
        }

        private static void TryLoadLedger()
        {
            if (_ledgerLoaded) return;
            var path = LedgerPath();
            if (path == null) return;
            _ledgerLoaded = true;
            try
            {
                if (!System.IO.File.Exists(path)) return;
                // Never overwrite a LIVE ledger: the session name can appear
                // late (first save), and loading an old file then would wipe
                // loans created meanwhile.
                if (_hostLoans.Count > 0)
                {
                    Plugin.Logger.LogInfo("[Hub] ledger file skipped — in-memory ledger already live.");
                    return;
                }
                var st = System.Text.Json.JsonSerializer.Deserialize<LoanStatePayload>(System.IO.File.ReadAllText(path));
                if (st == null) return;
                _hostLoans.Clear();
                _hostLoans.AddRange(st.Loans);
                HostBroadcastLoans();
                Plugin.Logger.LogInfo($"[Hub] ledger loaded ({_hostLoans.Count} loan(s)).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Hub] TryLoadLedger: {ex.Message}"); }
        }

        private static void HostBroadcastLoans()
        {
            var st = new LoanStatePayload();
            st.Loans.AddRange(_hostLoans);
            ApplyLoanState(st);              // host's own UI
            MPServer.BroadcastHub(MessageType.LoanState, st);
        }

        /// <summary>HOST: credit/debit a player's wallet wherever it lives.</summary>
        private static void DeliverMoney(string playerId, float amount, string reason, bool silent = false)
        {
            if (playerId == MPConfig.PlayerId) ApplyMoneyDelta(amount, reason, !silent);
            else MPServer.SendHubTo(playerId, MessageType.MoneyAdjust,
                     new MoneyAdjustPayload { To = playerId, Amount = amount, Reason = reason, Silent = silent });
        }
    }
}
