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

        // ── Send money ────────────────────────────────────────────────────────
        public static bool SendMoney(string to, float amount)
        {
            if (amount <= 0 || string.IsNullOrEmpty(to) || to == MPConfig.PlayerId) return false;
            if (MyMoney() < amount)
            {
                MPChat.AddNotice($"not enough money to send ${amount:N0}");
                return false;
            }
            ApplyMoneyDelta(-amount, $"sent to {to}");
            var p = new MoneyTransferPayload { From = MPConfig.PlayerId, To = to, Amount = amount };
            if (MPServer.IsRunning) HostRouteTransfer(p);
            else MPClient.SendHub(MessageType.MoneyTransfer, p);
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

        /// <summary>An offer arrived for ME (main thread).</summary>
        public static void ReceiveOffer(LoanOfferPayload p)
        {
            if (p == null || p.To != MPConfig.PlayerId) return;
            IncomingOffers.Add(p);
            MPChat.AddNotice($"{p.From} offers you a ${p.Principal:N0} loan — see the Business Hub");
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
            if (!a.Accept)
            {
                MPServer.BroadcastChat("Hub", $"{offer.To} declined {offer.From}'s loan offer.");
                return;
            }
            // Principal moves lender → borrower.
            DeliverMoney(offer.From, -offer.Principal, $"loan principal to {offer.To}");
            DeliverMoney(offer.To, offer.Principal, $"loan from {offer.From}");
            _hostLoans.Add(new LoanEntry
            {
                Id = offer.Id, Lender = offer.From, Borrower = offer.To,
                Remaining = offer.Principal,
                DailyInterest = offer.DailyInterest, DailyPayment = offer.DailyPayment,
            });
            HostBroadcastLoans();
        }

        /// <summary>HOST tick (main thread): day rollover drafts loan payments.</summary>
        public static void HostTick()
        {
            if (!MPServer.IsRunning) return;
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
                    MPServer.BroadcastChat("Hub", $"{ln.Borrower} has fully repaid {ln.Lender}'s loan.");
                    _hostLoans.RemoveAt(i);
                }
            }
            if (changed) HostBroadcastLoans();
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
