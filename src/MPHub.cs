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
        public static int PendingCount => IncomingOffers.Count;
        public static int Version;                 // bump = UI refresh + badge

        public static void Reset()
        {
            Loans.Clear();
            IncomingOffers.Clear();
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

        /// <summary>MAIN THREAD: adjust the local wallet + notice.</summary>
        public static void ApplyMoneyDelta(float delta, string reason)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;
                gi.Money += delta;
                MPChat.AddNotice($"{(delta >= 0 ? "+" : "−")}${Mathf.Abs(delta):N0} — {reason}");
                Plugin.Logger.LogInfo($"[Hub] money {(delta >= 0 ? "+" : "")}{delta:N0} ({reason}); balance {gi.Money:N0}.");
                Version++;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Hub] ApplyMoneyDelta: {ex.Message}"); }
        }

        // ── Gifts (accept-required: no silent handouts; acceptance = receipt) ─
        public static bool OfferGift(string to, float amount)
        {
            if (amount <= 0 || string.IsNullOrEmpty(to) || to == MPConfig.PlayerId) return false;
            var p = new LoanOfferPayload
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                From = MPConfig.PlayerId, To = to,
                Principal = amount, Kind = "gift",
            };
            MPChat.AddNotice($"gift of ${amount:N0} offered to {to} (awaiting their accept)");
            if (MPServer.IsRunning) HostRouteOffer(p);
            else MPClient.SendHub(MessageType.LoanOffer, p);
            return true;
        }

        // ── Loans: offer / answer (any role) ──────────────────────────────────
        public static void OfferLoan(string to, float principal, float dailyInterest, float dailyPayment)
        {
            if (principal <= 0 || string.IsNullOrEmpty(to) || to == MPConfig.PlayerId) return;
            if (MyMoney() < principal)
            {
                MPChat.AddNotice($"not enough money to offer a ${principal:N0} loan");
                return;
            }
            var p = new LoanOfferPayload
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                From = MPConfig.PlayerId, To = to,
                Principal = principal, DailyInterest = dailyInterest, DailyPayment = dailyPayment,
            };
            MPChat.AddNotice($"loan offered to {to}: ${principal:N0} (${dailyInterest:N0}/day interest, ${dailyPayment:N0}/day payment)");
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

        /// <summary>Daily-interest %% above this = the receiver gets a clear
        /// "predatory rate" warning on the offer (still acceptable).</summary>
        public const float PredatoryDailyPct = 3f;

        public static float OfferDailyPct(LoanOfferPayload o)
            => o.Principal > 0 ? o.DailyInterest / o.Principal * 100f : 0f;

        /// <summary>An offer arrived for ME (main thread).</summary>
        public static void ReceiveOffer(LoanOfferPayload p)
        {
            if (p == null || p.To != MPConfig.PlayerId) return;
            IncomingOffers.Add(p);
            if (p.Kind == "gift")
                MPChat.AddNotice($"{p.From} offers you a ${p.Principal:N0} GIFT — see the Business Hub");
            else
            {
                float pct = OfferDailyPct(p);
                MPChat.AddNotice($"{p.From} offers you a ${p.Principal:N0} loan at {pct:F1}%/day — see the Business Hub"
                                 + (pct > PredatoryDailyPct ? "  (PREDATORY RATE!)" : ""));
            }
            Version++;
        }

        public static void ApplyLoanState(LoanStatePayload? p)
        {
            if (p == null) return;
            Loans.Clear();
            Loans.AddRange(p.Loans);
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
            _hostOffers[p.Id] = p;
            if (p.To == MPConfig.PlayerId) ReceiveOffer(p);
            else MPServer.SendHubTo(p.To, MessageType.LoanOffer, p);
        }

        public static void HostHandleAnswer(LoanAnswerPayload a)
        {
            if (a == null || !_hostOffers.TryGetValue(a.Id, out var offer)) return;
            _hostOffers.Remove(a.Id);
            string what = offer.Kind == "gift" ? "gift" : "loan";
            if (!a.Accept)
            {
                NotifyParty(offer.From, $"{offer.To} declined your ${offer.Principal:N0} {what} offer.");
                return;
            }
            // Principal moves sender → receiver (overdraft allowed, like the bank).
            DeliverMoney(offer.From, -offer.Principal, $"{what} to {offer.To} (accepted)");
            DeliverMoney(offer.To, offer.Principal, $"{what} from {offer.From}");
            if (offer.Kind != "gift")
            {
                _hostLoans.Add(new LoanEntry
                {
                    Id = offer.Id, Lender = offer.From, Borrower = offer.To,
                    Remaining = offer.Principal,
                    DailyInterest = offer.DailyInterest, DailyPayment = offer.DailyPayment,
                });
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
                    DeliverMoney(ln.Borrower, -due, $"loan payment to {ln.Lender}");
                    DeliverMoney(ln.Lender, due, $"loan payment from {ln.Borrower}");
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

        public static void SaveLedger()
        {
            try
            {
                var path = LedgerPath();
                if (path == null) return;
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
        private static void DeliverMoney(string playerId, float amount, string reason)
        {
            if (playerId == MPConfig.PlayerId) ApplyMoneyDelta(amount, reason);
            else MPServer.SendHubTo(playerId, MessageType.MoneyAdjust,
                     new MoneyAdjustPayload { To = playerId, Amount = amount, Reason = reason });
        }
    }
}
