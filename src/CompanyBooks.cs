using Buildings;
using Entities;

namespace BigAmbitionsMP
{
    /// <summary>
    /// MERGER PHASE 4a — COMPANY BOOKS (D14/D18/D19, user-decided 2026-09-11).
    ///
    /// ONE LEVER, NOT EIGHT.  Every per-player money surface in the game reads the SAME stored list
    /// (GameInstance.financialSummaries): the daily popup, the top-bar arrow, EconoView's rows and
    /// totals, the persona page, the 7-day chart, the bank's loan cap, the tax page and BizMan's
    /// average income.  The authority veil (MPPatches Patch_MergerAuthorityVeil, step
    /// "Helpers.FinancialSummaryHelper/CreateFinancialSummary") makes each member's record hold ONLY
    /// its own businesses, so this file adds the partners' rows back exactly once and folds their
    /// whole day record into the totals — and every one of those surfaces becomes a company figure
    /// with no per-surface patch.
    ///
    /// D19 (the user's rulings this file implements):
    ///   1. EVERYTHING PAID FROM THE SHARED WALLET COUNTS — every float the day record holds is
    ///      folded, business and personal alike, with a per-record LEDGER of what was added per
    ///      partner so it can be removed exactly.
    ///   2. TAX stays per owner in the RECORD: the overlay is INERT (physically lifted) while the
    ///      merger veil is up, so the veiled Steps — GenerateTaxes and CreateFinancialSummary — see
    ///      own books only.  B9 is the pay-all on top of that.
    ///   3. The BANK LOAN CAP reads own income only (PlayerHelper.CalculateDailyIncome is answered
    ///      from the ledger-subtracted figure).
    ///   4. NET WORTH shows the company sums; each member publishes its three components.
    ///   5. The transaction feed is phase 4b — gi.Transactions is NOT touched here.
    ///   6. The residential line and the loans/insurance/salary group line carry NO colour.
    ///
    /// INERTNESS: nothing runs without a merger.  A non-member's call is one bool read.  Every
    /// refusal logs why ([Merger]/[SharedShop] house style) — there are no silent returns.
    /// </summary>
    public static class CompanyBooks
    {
        /// <summary>A bundle bigger than this is refused rather than shipped (the paperwork cap).</summary>
        public const int MaxBundleBytes = 2 * 1024 * 1024;

        /// <summary>Days of history one member publishes (B1).</summary>
        public const int HistoryDays = 30;

        // ── MEMBER STATE ────────────────────────────────────────────────────────────────────────
        /// <summary>Partner books as last received, keyed by the OWNER's player id.</summary>
        private static readonly Dictionary<string, CompanyBooksPayload> _partner = new();

        /// <summary>TAXBILL-ONE T5: the co-members' bundles as this machine holds them (lever read-out).</summary>
        public static IReadOnlyDictionary<string, CompanyBooksPayload> Partners => _partner;

        /// <summary>TAXBILL-ONE T1: the return THIS machine last FILED, snapshotted at filing time
        /// (MPPatches.Patch_TaxHelper_GenerateTaxes_Capture).  Two reasons it is not read back off the
        /// game: a ZERO bill leaves NO record at all (TaxHelper.cs:117-125), and a partial payment
        /// lowers the live record's totalToPay (TaxHelper.cs:409-411) while leaving its subtotals alone,
        /// which would make the company bill's lines and its grand total disagree.  Session fact -
        /// cleared with _partner in Reset().</summary>
        public static Taxes? LastFiledReturn;

        /// <summary>TAXBILL-ONE T2: true while the bill's row labels may carry a rich-text colour tag -
        /// set by the bill patch from what the renderer will actually do with a label.  False everywhere
        /// else, and the labels are then plain display names.
        /// GAME-PATCH-0916: false on THIS game build in every case - TaxesMessage.AddPlainLine wraps a
        /// non-key label in &lt;noparse&gt;...&lt;/noparse&gt; (decompile TaxesMessage.cs:366-375), so a tag would
        /// print literally.  See Patch_TaxesMessage_CompanyBill.LabelsAcceptMarkup.</summary>
        public static bool TaxRowTint;

        /// <summary>TAXBILL-ONE T4: the anniversary day whose COMPANY assessment was declined because a
        /// co-member's books had not arrived yet; -1 = nothing pending.  Session fact.</summary>
        public static int AnniversaryPending = -1;

        /// <summary>What THIS machine added to each day record, so it can be removed exactly.
        /// day → ownerPid → ledger entry.  This is the only record of the overlay: the game's own
        /// objects are mutated in place (every reader holds the live object).</summary>
        private sealed class Ledger
        {
            public readonly List<string> Addresses = new();      // business rows added
            public readonly List<string> RealEstate = new();     // real-estate rows added
            public float BusinessProfit, LoanExpenses, HealthInsurance, HeadhunterFees, RealEstateTotal;
            public float NegativeInterest, ParkingFees, SalaryIncome, ResidentialExpenses, UnassignedWages, TotalProfit;
        }
        private static readonly Dictionary<int, Dictionary<string, Ledger>> _overlaid = new();

        /// <summary>addressKey → owner pid, for the row tint (B5/D18) and the testdrive verb.</summary>
        private static readonly Dictionary<string, string> _rowOwner = new();
        /// <summary>Row LABEL (business name or formatted address) → owner pid: EconoView's cell view
        /// binds by rowName, not by address, so the tint needs this second map.</summary>
        private static readonly Dictionary<string, string> _labelOwner = new();

        private static int   _suspend;          // >0 = the overlay is physically lifted
        private static bool  _pending;          // books arrived / day changed: re-apply before the next display
        private static bool  _wasMember;
        private static int   _inertLoggedDay = -1;
        private static int   _lastPublishedDay = -1;
        private static int   _veilBuildLoggedDay = -1;   // MINOR (c): the build-under-veil note is once per DAY, not per call

        /// <summary>M0 (review r2): the membership EDGE this machine has already published for - the
        /// group id plus its SORTED member set, so a third member joining or leaving is an edge too.
        /// Empty = not in a company.  A refused publish leaves it UNCHANGED, so the edge re-arms.</summary>
        private static string _memberSig = "";
        private static string _edgeHeldSig = "";
        private static float  _edgeRetryAt;   // r4: next retry time for a refused membership-edge publish

        /// <summary>M6 (review r2): addresses whose NATIVE row this machine kept instead of overlaying
        /// the partner's copy - logged once each.</summary>
        private static readonly HashSet<string> _nativeKept = new();

        /// <summary>The overlay is inert while the authority veil is up (D19-2) or a save strip is in
        /// progress (B3).  MergerFlip drives both through SuspendPush/SuspendPop.</summary>
        public static bool Inert => _suspend > 0 || MergerFlip.VeilDepth > 0;

        public static int OverlaidDays => _overlaid.Count;
        public static int PartnerCount => _partner.Count;

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // B1 — PUBLISH (this member's own books)
        // ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>MAIN THREAD.  Build this machine's own last-30-day books.  Statements cover the
        /// addresses this machine actually OWNS (TrulyMine) plus the ones it SIMULATES for an absent
        /// member — during an absence the veil is lifted for the simulated addresses (MergerFlip.Push
        /// calls ApplyAll(flip:false, honourSimulated:true) for a VeilPush), so the simulator's own
        /// record carries them and the host re-keys them to the real owner (HostStore).</summary>
        public static CompanyBooksPayload? Build()
        {
            var gi = SaveGameManager.Current;
            if (gi?.financialSummaries == null) { Plugin.Logger.LogInfo("[Books] build skipped: no game instance yet."); return null; }

            var p = new CompanyBooksPayload
            {
                OwnerPid = MPConfig.PlayerId,
                StableId = MPConfig.StableId,
                Day      = gi.Day,
            };

            int firstDay = gi.Day - HistoryDays;
            bool anyVeil = MergerFlip.VeilDepth > 0;
            if (anyVeil && gi.Day != _veilBuildLoggedDay)
            {   // MINOR (c): this is the NORMAL day-change path (RunDaily is itself a veil Step), not a fault - Info, once per day.
                _veilBuildLoggedDay = gi.Day;
                Plugin.Logger.LogInfo("[Books] build ran with the veil up — the record is own-only, which is what we publish (no refusal).");
            }

            foreach (var s in gi.financialSummaries)
            {
                if (s == null || s.dayNumber < firstDay) continue;

                p.Totals.Add(new CbDayTotals
                {
                    Day                 = s.dayNumber,
                    BusinessProfit      = s.totalBusinessProfit,
                    LoanExpenses        = s.totalLoanExpenses,
                    HealthInsurance     = s.totalHealthInsuranceExpenses,
                    HeadhunterFees      = s.totalHeadhunterReplacementFees,
                    RealEstate          = s.totalRealEstate,
                    NegativeInterest    = s.negativeInterestRates,
                    ParkingFees         = s.parkingFees,
                    SalaryIncome        = s.salaryIncome,
                    ResidentialExpenses = s.totalResidentialExpenses,
                    UnassignedWages     = s.totalUnassignedStaffWages,
                    TotalProfit         = s.totalProfit,
                });

                if (s.businessIncomeStatements != null)
                    foreach (var b in s.businessIncomeStatements)
                    {
                        if (b == null) continue;
                        string key;
                        try { key = GameStateReader.AddressKey(b.Address); } catch { continue; }
                        if (string.IsNullOrEmpty(key)) continue;
                        if (!PublishableHere(key)) continue;
                        p.Statements.Add(ToDto(b, s.dayNumber, key));
                    }

                if (s.realEstateStatements != null)
                    foreach (var r in s.realEstateStatements)
                    {
                        if (r == null) continue;
                        string key;
                        try { key = GameStateReader.AddressKey(r.Address); } catch { continue; }
                        if (string.IsNullOrEmpty(key)) continue;
                        p.RealEstate.Add(new CbRealEstate { Day = s.dayNumber, AddressKey = key, Amount = r.Amount });
                    }
            }

            // D19-4: the three net-worth components this member contributes (the page shows the sums).
            try
            {
                var w = Helpers.PlayerHelper.GetPersonalWealth();
                p.Investments    = w.totalInvestments;
                p.LoansRemaining = w.totalLoans;
                p.AssetsWorth    = w.totalAssets;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] wealth components unavailable: {ex.Message} — the page will show this member as 0."); }

            // B9-ii: the outstanding tax bill as the GAME holds it.
            try
            {
                // T9 (phase 4b): the CURRENT-period figure is published beside the whole outstanding
                // one - the tax page's single amount label natively shows the current bill only, so the
                // company total must sum current-only halves (back taxes have their own label).
                p.TaxCurrentDue  = Helpers.TaxHelper.GetCurrentTaxesToPay();
                p.TaxDue         = p.TaxCurrentDue + Helpers.TaxHelper.GetBackTaxesToPay();
                p.TaxDeadlineDay = Helpers.TaxHelper.GetCurrentTaxesDueDay();
                p.TaxPeriod      = gi.currentUnpaidTaxes?.day ?? 0;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] tax figure unavailable: {ex.Message} — this member contributes 0 to the company bill."); }

            // TAXBILL-ONE T1: the member's CURRENT filed return - the parts the bill renderer draws.
            // Preferred source is the snapshot taken at filing; the fallback (a bill filed before this
            // machine joined, or before the mod loaded) is the live record with its total REBUILT from
            // its own subtotals by the game's formula (GenerateTaxes, TaxHelper.cs:268-272), which is
            // the as-filed figure a partial payment has since lowered.
            try
            {
                int dpyR = gi.gameVariables?.daysPerYear ?? 0;
                var filed = LastFiledReturn;
                Taxes? src = null;
                bool asFiled = false;
                if (filed != null && dpyR > 0 && filed.day / dpyR == gi.Day / dpyR) { src = filed; asFiled = true; }
                else if (gi.currentUnpaidTaxes != null) src = gi.currentUnpaidTaxes;
                if (src != null) p.TaxReturn = ToReturnDto(src, asFiled);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] filed return unavailable: {ex.Message} — this member contributes no rows to the company bill."); }

            // TAXBILL-ONE T1: this member's OWN sales over the tax year, for the company's filing line.
            // ORDER: this is the LAST thing Build does.  OwnLastYearSales lifts the overlay, and the
            // matching SuspendPop can RE-APPLY it (CompanyBooks.cs:816-824) - every other field must
            // already have been taken from the own-only record by the time that happens.
            try { p.LastYearSales = OwnLastYearSales(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] own last-year sales unavailable: {ex.Message} — this member contributes 0 to the company filing line."); }

            return p;
        }

        /// <summary>An address whose books THIS machine is the right publisher for.</summary>
        private static bool PublishableHere(string key)
        {
            try { if (MergerAbsence.SimulatesHere(key)) return true; } catch { }
            try
            {
                foreach (var reg in SaveGameManager.Current.BuildingRegistrations)
                    if (reg != null && GameStateReader.AddressKey(reg) == key) return MergerFlip.TrulyMine(reg);
            }
            catch { }
            return false;
        }

        private static CbStatement ToDto(FinancialSummary.BusinessIncomeStatement b, int day, string key)
        {
            var d = new CbStatement
            {
                Day = day, AddressKey = key,
                SalaryExpenses = b.SalaryExpenses, RentExpenses = b.RentExpenses,
                MarketingExpenses = b.MarketingExpenses, Theft = b.Theft, LicensingFees = b.LicensingFees,
                TotalSales = b.TotalSales, TotalResources = b.TotalResources,
                TotalOngoing = b.TotalOngoing, TotalProfit = b.TotalProfit,
            };
            if (b.Sales != null) foreach (var e in b.Sales) if (e != null) d.Sales.Add(new CbTxGroup { ItemName = e.ItemName ?? "", Amount = e.Amount });
            if (b.Resources != null) foreach (var e in b.Resources) if (e != null) d.Resources.Add(new CbTxGroup { ItemName = e.ItemName ?? "", Amount = e.Amount });
            return d;
        }

        /// <summary>MAIN THREAD.  Publish, unless there is nothing to publish to or the bundle is
        /// over the cap.  Every refusal logs why.  TRUE only when the bundle actually went out - the
        /// membership edge is consumed on that, never on a refusal (m-d, review r3).</summary>
        public static bool Publish(string why)
        {
            try
            {
                if (!MergerSync.IAmMember)
                {
                    if (_wasMember) Plugin.Logger.LogInfo($"[Books] publish ({why}) refused: this machine is not in a merged company.");
                    return false;
                }
                if (!MPWorldReady.IsSettled) { Plugin.Logger.LogInfo($"[Books] publish ({why}) refused: the world is not settled yet."); return false; }

                // r4 (re-check r3 MAJOR): Build reads the LIVE records, and between edges the partners'
                // figures are folded into them - a publish taken then would leave this machine carrying
                // a partner's profit as its own (and the host would fan it on).  Lift the overlay HERE,
                // in the one place every caller passes through; the callers re-apply afterwards.
                if (_overlaid.Count > 0) { RemoveAll(); _pending = true; }

                var p = Build();
                if (p == null) return false;

                string json;
                try { json = Newtonsoft.Json.JsonConvert.SerializeObject(p); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] publish ({why}) refused: serialise failed — {ex.Message}."); return false; }

                int bytes = System.Text.Encoding.UTF8.GetByteCount(json);
                if (bytes > MaxBundleBytes)
                {
                    if (p.Day != _lastPublishedDay)
                        Plugin.Logger.LogWarning($"[Books] publish ({why}) REFUSED: {bytes} bytes > {MaxBundleBytes} cap — nothing sent; the host's previously stored books stand.");
                    _lastPublishedDay = p.Day;
                    return false;
                }

                if (MPServer.IsRunning)      HostStore(p, MPConfig.PlayerId);          // the host is a member too
                else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CompanyBooks, MPConfig.PlayerId, p));
                else { Plugin.Logger.LogInfo($"[Books] publish ({why}) refused: not in a session."); return false; }

                _lastPublishedDay = p.Day;
                Plugin.Logger.LogInfo($"[Books] published day {p.Day}: {p.Statements.Count} statements ({bytes} bytes, {why}).");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] publish ({why}): {ex.Message}"); }
            return false;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // B1 — HOST STORE + FAN-OUT + JOIN REPLAY
        // ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>ownerPid → that owner's latest books, as the host holds them.  G1 (review r2): the
        /// store also rides the session manifest (MpManifest.CompanyBooks, beside Paperwork/Absence) on
        /// exactly the same timeline - written with the slot, REPLACED from the loaded slot on every
        /// load, reset on a new world, never carried over in memory.  Every member also republishes at
        /// its next day change and on every membership edge, so a gap self-heals inside one game day.</summary>
        private static readonly Dictionary<string, CompanyBooksPayload> _store = new();

        public static void HostReset()
        {
            lock (_store) _store.Clear();
            lock (_held) _held.Clear();   // M4 (r2): a pay-all held for an offline member must not outlive the session/world boundary
            Plugin.Logger.LogInfo("[Books] host store cleared.");
        }

        /// <summary>G1, HOST: a LOCKED copy of the store for the manifest snapshot (the persist path is
        /// not guaranteed main-thread, so it must never enumerate the live dictionary).</summary>
        public static List<KeyValuePair<string, CompanyBooksPayload>> HostStoreSnapshot()
        {
            lock (_store) return new List<KeyValuePair<string, CompanyBooksPayload>>(_store);
        }

        /// <summary>G1, HOST: put one bundle back from the manifest being restored (clear-then-apply -
        /// the caller clears first, exactly as the paperwork restore does).</summary>
        public static void HostRestore(CompanyBooksPayload p)
        {
            // m-c (review r3): a player id dies with the session, so a bundle whose owner is not back
            // yet has NONE.  It is kept under its STABLE id and adopted the moment that member
            // connects (HostAdoptStables) - that is what puts it back in the fan-out and the replay.
            if (p == null) return;
            string key = !string.IsNullOrEmpty(p.OwnerPid) ? p.OwnerPid : p.StableId;
            if (string.IsNullOrEmpty(key)) return;
            PutStore(key, p);
        }

        /// <summary>HOST: store one bundle under one key and drop any OTHER entry carrying the same
        /// stable id (m-c, r3: a stable-keyed restore and that member's later publish must never stand
        /// as two bundles for one person - both would be fanned out and both would ride the manifest).</summary>
        private static void PutStore(string key, CompanyBooksPayload p)
        {
            lock (_store)
            {
                if (p != null && !string.IsNullOrEmpty(p.StableId))
                {
                    List<string>? dup = null;
                    foreach (var kv in _store)
                        if (kv.Key != key && kv.Value != null && kv.Value.StableId == p.StableId)
                            (dup ??= new List<string>()).Add(kv.Key);
                    if (dup != null)
                        foreach (var d in dup)
                        {
                            _store.Remove(d);
                            Plugin.Logger.LogInfo($"[Books] duplicate bundle dropped under '{d}': stable id '{p.StableId}' now stands under '{key}'.");
                        }
                }
                _store[key] = p;
            }
        }

        /// <summary>HOST: re-key every stable-keyed bundle whose owner is now online.  Runs wherever
        /// the store is about to be used by player id (a member publishes, a member joins), so a
        /// restored bundle needs no hook of its own in the connect path.</summary>
        private static void HostAdoptStables()
        {
            try
            {
                if (!MPServer.IsRunning) return;
                List<CompanyBooksPayload>? waiting = null;
                lock (_store)
                {
                    foreach (var kv in _store)
                    {
                        var b = kv.Value;
                        if (b == null || !string.IsNullOrEmpty(b.OwnerPid) || string.IsNullOrEmpty(b.StableId)) continue;
                        (waiting ??= new List<CompanyBooksPayload>()).Add(b);
                    }
                    if (waiting == null) return;
                    foreach (var b in waiting)
                    {
                        string pid = "";
                        foreach (var sp in MPServer.StableIdByPlayer)
                            if (sp.Value == b.StableId) { pid = sp.Key; break; }
                        if (string.IsNullOrEmpty(pid)) continue;           // still not online: it waits
                        _store.Remove(b.StableId);
                        if (_store.TryGetValue(pid, out var live) && live != null)
                        {
                            Plugin.Logger.LogInfo($"[Books] duplicate bundle dropped under '{b.StableId}': '{pid}' has already published this session.");
                            continue;
                        }
                        b.OwnerPid = pid;
                        PutStore(pid, b);
                        Plugin.Logger.LogInfo($"[Books] adopted the restored bundle of stable '{b.StableId}' as '{pid}' - it fans out and replays from now on.");
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] adopt restored bundles: {ex.Message}"); }
        }

        /// <summary>HOST, MAIN THREAD.  Store one member's books, re-keying any statement whose
        /// address belongs to an ABSENT owner this sender simulates, then fan out to the owner's
        /// online co-members.</summary>
        public static void HostStore(CompanyBooksPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(senderPid)) { Plugin.Logger.LogWarning("[Books] store refused: empty payload or sender."); return; }
                HostAdoptStables();   // m-c: a bundle restored for this sender (or for an owner it files for) is keyed by pid again before any lookup below
                if (!MergerSync.InAnyGroup(senderPid)) { Plugin.Logger.LogInfo($"[Books] store refused for '{senderPid}': not in a company."); return; }

                p.OwnerPid = senderPid;
                foreach (var filed in HostFileSimulated(p, senderPid))
                {
                    CompanyBooksPayload fanned;
                    lock (_store)
                    {
                        if (_store.TryGetValue(filed.OwnerPid, out var have) && have != null) { MergeFiled(have, filed); fanned = have; }
                        else
                        {
                            PutStore(filed.OwnerPid, filed); fanned = filed;
                            Plugin.Logger.LogInfo($"[Books] '{filed.OwnerPid}' had no stored bundle — the simulated statements become its first one (its wealth and tax figures stay 0 until it publishes).");
                        }
                    }
                    HostFanOut(fanned);
                }

                PutStore(senderPid, p);
                Plugin.Logger.LogInfo($"[Books] stored for '{senderPid}' (day {p.Day}, {p.Statements.Count} statements).");
                HostFlushHeld(senderPid);   // (i): a pay-all held for this member can now be period-checked against the bundle it has just published
                HostFanOut(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] store: {ex.Message}"); }
        }

        /// <summary>Split out the statements of ABSENT owners this sender simulates and file each as
        /// that owner's own books.  The day TOTALS cannot be split field by field (the simulator's
        /// personal lines are its own), so exactly the simulated statements' TotalProfit moves from
        /// the simulator's business profit and total profit into the owner's — which is what the
        /// shared wallet actually paid out for those shops.</summary>
        private static List<CompanyBooksPayload> HostFileSimulated(CompanyBooksPayload p, string senderPid)
        {
            var made = new List<CompanyBooksPayload>();
            try
            {
                if (MergerAbsence.MarkCount == 0) return made;

                var ownerOf = new Dictionary<string, string>();
                foreach (var kv in MergerAbsence.Marks)
                {
                    var m = kv.Value;
                    if (m == null || string.IsNullOrEmpty(m.OwnerPid) || m.OwnerPid == senderPid) continue;
                    if (m.Addresses == null) continue;
                    foreach (var a in m.Addresses) if (!string.IsNullOrEmpty(a)) ownerOf[a] = m.OwnerPid;
                }
                if (ownerOf.Count == 0) return made;

                var stableOf = new Dictionary<string, string>();
                foreach (var kv in MergerAbsence.Marks)
                {
                    var mk = kv.Value;
                    if (mk == null || string.IsNullOrEmpty(mk.OwnerPid) || string.IsNullOrEmpty(mk.OwnerStable)) continue;
                    stableOf[mk.OwnerPid] = mk.OwnerStable;   // G1: a filed bundle needs the owner's STABLE id to reach the manifest
                }

                var byOwner = new Dictionary<string, CompanyBooksPayload>();
                var moved   = new Dictionary<string, Dictionary<int, float>>();

                for (int i = p.Statements.Count - 1; i >= 0; i--)
                {
                    var st = p.Statements[i];
                    if (st == null || !ownerOf.TryGetValue(st.AddressKey, out var owner)) continue;
                    p.Statements.RemoveAt(i);
                    if (!byOwner.TryGetValue(owner, out var op))
                    {
                        op = new CompanyBooksPayload { OwnerPid = owner, Day = p.Day, StableId = stableOf.TryGetValue(owner, out var ost) ? ost : "" };
                        byOwner[owner] = op; moved[owner] = new Dictionary<int, float>();
                    }
                    op.Statements.Add(st);
                    moved[owner].TryGetValue(st.Day, out var had);
                    moved[owner][st.Day] = had + st.TotalProfit;
                }

                foreach (var kv in byOwner)
                {
                    var op = kv.Value;
                    foreach (var dayKv in moved[kv.Key])
                    {
                        op.Totals.Add(new CbDayTotals { Day = dayKv.Key, BusinessProfit = dayKv.Value, TotalProfit = dayKv.Value });
                        foreach (var t in p.Totals)
                            if (t.Day == dayKv.Key) { t.BusinessProfit -= dayKv.Value; t.TotalProfit -= dayKv.Value; }
                    }
                    made.Add(op);
                    Plugin.Logger.LogInfo($"[Books] filed {op.Statements.Count} statements of '{kv.Key}' from '{senderPid}'.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] simulated filing for '{senderPid}': {ex.Message}"); }
            return made;
        }

        /// <summary>HOST, M3 (review r2): fold one simulator-FILED bundle into the absent owner's
        /// STORED bundle.  A wholesale replace destroyed that owner's wealth and tax figures (HostPayAll
        /// then skipped it, TaxDue &lt;= 0) and threw away its 30-day history, so only the filed days and
        /// addresses move.  REPLACEMENT, not addition, at both levels: the simulator republishes on every
        /// day change and every membership edge, and an additive merge would double-count each time.  The
        /// machine that actually RAN a day is the authority for the MOVED ADDRESSES' share of that
        /// day's totals - m-a (r3): for those addresses only, never for the owner's whole day.</summary>
        private static void MergeFiled(CompanyBooksPayload have, CompanyBooksPayload filed)
        {
            try
            {
                // m-a (review r3): what the owner's STORED bundle already carried for the moved
                // addresses on each day - the filed record is the authority for those addresses only,
                // never for the whole day (the owner's personal lines are its own).
                var priorMoved = new Dictionary<int, float>();
                foreach (var st in filed.Statements)
                {
                    if (st == null) continue;
                    string k = st.AddressKey; int d = st.Day;
                    foreach (var old in have.Statements)
                        if (old != null && old.Day == d && old.AddressKey == k)
                        { priorMoved.TryGetValue(d, out var q); priorMoved[d] = q + old.TotalProfit; }
                    have.Statements.RemoveAll(x => x != null && x.Day == d && x.AddressKey == k);
                    have.Statements.Add(st);
                }
                // (ii) carried over from the 4a review: priorMoved covered the BUSINESS statements
                // only, so a filed REAL-ESTATE row was swapped into the stored bundle while the day's
                // RealEstate and TotalProfit still carried whatever that bundle held for the same
                // address - the moved row counted twice (or, where the owner had none, never).  Its
                // share moves exactly like a business row's: had - prior + filed, on the day's
                // RealEstate line and on TotalProfit, of which it is a component.
                var priorMovedRe = new Dictionary<int, float>();
                var filedRe      = new Dictionary<int, float>();
                foreach (var re in filed.RealEstate)
                {
                    if (re == null) continue;
                    string k = re.AddressKey; int d = re.Day;
                    foreach (var old in have.RealEstate)
                        if (old != null && old.Day == d && old.AddressKey == k)
                        { priorMovedRe.TryGetValue(d, out var q); priorMovedRe[d] = q + old.Amount; }
                    filedRe.TryGetValue(d, out var f); filedRe[d] = f + re.Amount;
                    have.RealEstate.RemoveAll(x => x != null && x.Day == d && x.AddressKey == k);
                    have.RealEstate.Add(re);
                }
                foreach (var t in filed.Totals)
                {
                    if (t == null) continue;
                    int d = t.Day;
                    // m-a: a FILED record carries only BusinessProfit/TotalProfit (HostFileSimulated).
                    // Dropping the owner's whole day for it zeroed that day's personal lines until the
                    // owner republished, so when the owner HAS that day only the moved addresses'
                    // contribution is swapped: out with what it had for them, in with the filed figure.
                    var had = have.Totals.Find(x => x != null && x.Day == d);
                    if (had == null) { have.Totals.Add(t); continue; }
                    priorMoved.TryGetValue(d, out var prior);
                    had.BusinessProfit = had.BusinessProfit - prior + t.BusinessProfit;
                    had.TotalProfit    = had.TotalProfit    - prior + t.TotalProfit;
                    // (ii): the same swap for the day's real-estate line.  A filed bundle's own Totals
                    // carry only BusinessProfit/TotalProfit (HostFileSimulated :450), so the filed
                    // figure here is the sum of the rows that actually moved, not t.RealEstate.
                    priorMovedRe.TryGetValue(d, out var priorRe);
                    filedRe.TryGetValue(d, out var filedReAmt);
                    if (priorRe != 0f || filedReAmt != 0f)
                    {
                        had.RealEstate  = had.RealEstate  - priorRe + filedReAmt;
                        had.TotalProfit = had.TotalProfit - priorRe + filedReAmt;
                    }
                }
                if (filed.Day > have.Day) have.Day = filed.Day;
                if (string.IsNullOrEmpty(have.StableId) && !string.IsNullOrEmpty(filed.StableId)) have.StableId = filed.StableId;
                Plugin.Logger.LogInfo($"[Books] merged {filed.Statements.Count} simulated statement(s) into the stored bundle of '{have.OwnerPid}' (its wealth and tax figures kept).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] merge filed into '{have?.OwnerPid}': {ex.Message}"); }
        }

        /// <summary>HOST: send one owner's books to every ONLINE co-member of that owner's company
        /// (never back to the owner — a member's own books must not be added twice).</summary>
        private static void HostFanOut(CompanyBooksPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.OwnerPid)) return;
                int n = MPServer.SendCompanyBooksToGroup(p);
                if (n == 0) Plugin.Logger.LogInfo($"[Books] '{p.OwnerPid}' books stored but not fanned out: no other online member of that company.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] fan-out: {ex.Message}"); }
        }

        /// <summary>HOST: every stored bundle a JOINER is entitled to (its company's, not its own).</summary>
        public static List<CompanyBooksPayload> HostReplayFor(string joinerPid)
        {
            var outp = new List<CompanyBooksPayload>();
            try
            {
                if (string.IsNullOrEmpty(joinerPid)) return outp;
                HostAdoptStables();   // m-c: the joiner may BE the stable a restored bundle is waiting for, and its co-members' bundles must key by pid to be found here
                lock (_store)
                    foreach (var kv in _store)
                    {
                        if (kv.Key == joinerPid) continue;
                        if (!MergerSync.MergedRuntime(kv.Key, joinerPid)) continue;
                        outp.Add(kv.Value);
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] join replay for '{joinerPid}': {ex.Message}"); }
            return outp;
        }

        /// <summary>HOST: one member's stored tax bill (B9-iii on the host's own page).</summary>
        public static IReadOnlyDictionary<string, CompanyBooksPayload> Store => _store;

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // B2 — THE OVERLAY
        // ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>MAIN THREAD.  A partner's books arrived.</summary>
        public static void Receive(CompanyBooksPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.OwnerPid)) { Plugin.Logger.LogWarning("[Books] receipt refused: empty payload."); return; }
                if (p.OwnerPid == MPConfig.PlayerId) { Plugin.Logger.LogInfo("[Books] receipt refused: these are this machine's OWN books echoed back."); return; }
                if (!MergerSync.IsMemberPid(p.OwnerPid)) { Plugin.Logger.LogInfo($"[Books] receipt refused: '{p.OwnerPid}' is not in this machine's company."); return; }

                _partner[p.OwnerPid] = p;
                _pending = true;
                Apply("books receipt");
                TaxAnniversaryRecheck();   // TAXBILL-ONE T4: the late books may be what puts the company over the filing line
                try { MPPatches.RefreshTopbarMoneyChange(); } catch { }   // B4: the arrow/tooltip are event-driven
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] receive: {ex.Message}"); }
        }

        /// <summary>MAIN THREAD.  Remove everything this machine added, then add it again from the
        /// partner books currently held.  Idempotent by address — GetAvgDailyIncome divides by the
        /// matching row COUNT (BuildingRegistration.cs:363), so a duplicate row is not cosmetic.</summary>
        public static void Apply(string why)
        {
            try
            {
                if (Inert)
                {
                    int d = SaveGameManager.Current?.Day ?? -1;
                    if (d != _inertLoggedDay) { _inertLoggedDay = d; Plugin.Logger.LogInfo($"[Books] inert (veil depth {MergerFlip.VeilDepth}) — {why} deferred."); }
                    _pending = true;
                    return;
                }
                if (!MergerSync.IAmMember)
                {
                    if (_overlaid.Count > 0) ClearOverlay("no longer a member");
                    return;
                }

                RemoveAll();
                var gi = SaveGameManager.Current;
                if (gi?.financialSummaries == null) { Plugin.Logger.LogInfo($"[Books] apply ({why}) refused: no game instance."); return; }

                int rows = 0; float added = 0f;
                foreach (var kv in _partner)
                {
                    string pid = kv.Key; var p = kv.Value;
                    if (p == null) continue;
                    if (!MergerSync.IsMemberPid(pid)) { Plugin.Logger.LogInfo($"[Books] skipped '{pid}': no longer in this company."); continue; }

                    // Per-day skipped profit: an address THIS machine simulates is already in its own
                    // record, and the host moved that profit into the owner's totals — fold it once.
                    var skipped = new Dictionary<int, float>();
                    // M1 (review r3): the same correction for a REAL-ESTATE row skipped below.  Its
                    // amount is already in this machine's own totalRealEstate, so the partner's day
                    // total must be folded MINUS that row — it lands in RealEstate and in TotalProfit.
                    var skippedRealEstate = new Dictionary<int, float>();

                    foreach (var st in p.Statements)
                    {
                        if (st == null || string.IsNullOrEmpty(st.AddressKey)) continue;
                        if (IsOwnHere(st.AddressKey))
                        {
                            skipped.TryGetValue(st.Day, out var s0);
                            skipped[st.Day] = s0 + st.TotalProfit;
                            continue;
                        }
                        var rec = gi.financialSummaries.Find(x => x != null && x.dayNumber == st.Day);
                        if (rec == null) continue;                       // that day is not in this save's window
                        if (!TryAddressOf(st.AddressKey, out var addr)) continue;
                        rec.businessIncomeStatements ??= new List<FinancialSummary.BusinessIncomeStatement>();
                        string bKey = st.AddressKey;
                        // M6 (review r2): RemoveAll() above already took back every row THIS overlay added,
                        // so a row still standing for that address is a NATIVE one (a day this machine
                        // simulated the shop, whose mark has since been retired).  The overlay may only
                        // ever remove what it ADDED: keep the native row, skip the partner's copy, and fold
                        // that profit once (the native row's money is already in this machine's own totals).
                        if (rec.businessIncomeStatements.Find(x => x != null && GameStateReader.AddressKey(x.Address) == bKey) != null)
                        {
                            if (_nativeKept.Add(bKey)) Plugin.Logger.LogInfo($"[Books] kept this machine's OWN row for '{bKey}': the partner's statement for that address is not overlaid (it would fold that day's profit twice).");
                            skipped.TryGetValue(st.Day, out var sNat);
                            skipped[st.Day] = sNat + st.TotalProfit;
                            continue;
                        }
                        rec.businessIncomeStatements.Add(FromDto(st, addr));
                        LedgerOf(st.Day, pid).Addresses.Add(st.AddressKey);
                        _rowOwner[st.AddressKey] = pid;
                        LearnLabel(st.AddressKey, pid);
                        rows++;
                    }

                    foreach (var re in p.RealEstate)
                    {
                        if (re == null || string.IsNullOrEmpty(re.AddressKey)) continue;
                        if (IsOwnHere(re.AddressKey))
                        {
                            skippedRealEstate.TryGetValue(re.Day, out var r0);
                            skippedRealEstate[re.Day] = r0 + re.Amount;
                            continue;
                        }
                        var rec = gi.financialSummaries.Find(x => x != null && x.dayNumber == re.Day);
                        if (rec == null) continue;
                        if (!TryAddressOf(re.AddressKey, out var addr)) continue;
                        rec.realEstateStatements ??= new List<FinancialSummary.RealEstateStatement>();
                        string rKey = re.AddressKey;
                        if (rec.realEstateStatements.Find(x => x != null && GameStateReader.AddressKey(x.Address) == rKey) != null)
                        {   // M6: the same rule for a real-estate row this machine holds natively.
                            if (_nativeKept.Add(rKey)) Plugin.Logger.LogInfo($"[Books] kept this machine's OWN real-estate row for '{rKey}': the partner's copy is not overlaid.");
                            skippedRealEstate.TryGetValue(re.Day, out var rNat);
                            skippedRealEstate[re.Day] = rNat + re.Amount;
                            continue;
                        }
                        rec.realEstateStatements.Add(new FinancialSummary.RealEstateStatement { Address = addr, Amount = re.Amount });
                        LedgerOf(re.Day, pid).RealEstate.Add(re.AddressKey);
                        _rowOwner[re.AddressKey] = pid;
                        LearnLabel(re.AddressKey, pid);
                        rows++;
                    }

                    // D19-1: fold the partner's WHOLE day record — every float it holds.
                    var foldedDays = new List<int>();   // DAYTABLE-1 C3(ii)
                    foreach (var t in p.Totals)
                    {
                        if (t == null) continue;
                        var rec = gi.financialSummaries.Find(x => x != null && x.dayNumber == t.Day);
                        if (rec == null)
                        {
                            // DAYTABLE-1 C1: this day's whole partner total is dropped on the floor here.
                            // The user suspects MONEY is being missed, not only a display difference — this
                            // line is what two players' logs compare.  Behaviour unchanged (still `continue`).
                            if (_foldDropLines < 60)
                            {
                                _foldDropLines++;
                                Plugin.Logger.LogWarning($"[Books] fold DROPPED: no local day record for day {t.Day} from '{pid}' (their total {t.TotalProfit:F2})");
                            }
                            continue;
                        }
                        skipped.TryGetValue(t.Day, out var skip);
                        skippedRealEstate.TryGetValue(t.Day, out var skipRe);
                        var L = LedgerOf(t.Day, pid);
                        Fold(rec, L, t, skip, skipRe);
                        added += t.TotalProfit - skip - skipRe;
                        if (!foldedDays.Contains(t.Day)) foldedDays.Add(t.Day);
                    }
                    foreach (var fd in foldedDays) LogDayTable(fd, "fold");   // DAYTABLE-1 C3(ii)
                }

                _pending = false;
                Plugin.Logger.LogInfo($"[Books] overlaid {rows} rows over {_overlaid.Count} days (profit +{added:F2}) ({why}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] apply ({why}): {ex.Message}"); }
        }

        private static void Fold(FinancialSummary rec, Ledger L, CbDayTotals t, float skip, float skipRe)
        {
            float bp = t.BusinessProfit - skip, reAmt = t.RealEstate - skipRe, tp = t.TotalProfit - skip - skipRe;
            rec.totalBusinessProfit              += bp;                           L.BusinessProfit      += bp;
            rec.totalLoanExpenses                += t.LoanExpenses;               L.LoanExpenses        += t.LoanExpenses;
            rec.totalHealthInsuranceExpenses     += t.HealthInsurance;            L.HealthInsurance     += t.HealthInsurance;
            rec.totalHeadhunterReplacementFees   += t.HeadhunterFees;             L.HeadhunterFees      += t.HeadhunterFees;
            rec.totalRealEstate                  += reAmt;                        L.RealEstateTotal     += reAmt;
            rec.negativeInterestRates            += t.NegativeInterest;           L.NegativeInterest    += t.NegativeInterest;
            rec.parkingFees                      += t.ParkingFees;                L.ParkingFees         += t.ParkingFees;
            rec.salaryIncome                     += t.SalaryIncome;               L.SalaryIncome        += t.SalaryIncome;
            rec.totalResidentialExpenses         += t.ResidentialExpenses;        L.ResidentialExpenses += t.ResidentialExpenses;
            rec.totalUnassignedStaffWages        += t.UnassignedWages;            L.UnassignedWages     += t.UnassignedWages;
            rec.totalProfit                      += tp;                           L.TotalProfit         += tp;
        }

        private static void Unfold(FinancialSummary rec, Ledger L)
        {
            rec.totalBusinessProfit            -= L.BusinessProfit;
            rec.totalLoanExpenses              -= L.LoanExpenses;
            rec.totalHealthInsuranceExpenses   -= L.HealthInsurance;
            rec.totalHeadhunterReplacementFees -= L.HeadhunterFees;
            rec.totalRealEstate                -= L.RealEstateTotal;
            rec.negativeInterestRates          -= L.NegativeInterest;
            rec.parkingFees                    -= L.ParkingFees;
            rec.salaryIncome                   -= L.SalaryIncome;
            rec.totalResidentialExpenses       -= L.ResidentialExpenses;
            rec.totalUnassignedStaffWages      -= L.UnassignedWages;
            rec.totalProfit                    -= L.TotalProfit;
        }

        /// <summary>MAIN THREAD.  Remove every overlaid row and subtract every added float, exactly.</summary>
        private static void RemoveAll()
        {
            if (_overlaid.Count == 0) { _rowOwner.Clear(); _labelOwner.Clear(); return; }
            var gi = SaveGameManager.Current;
            try
            {
                foreach (var dayKv in _overlaid)
                {
                    var rec = gi?.financialSummaries?.Find(x => x != null && x.dayNumber == dayKv.Key);
                    if (rec == null) continue;
                    foreach (var pidKv in dayKv.Value)
                    {
                        var L = pidKv.Value;
                        foreach (var key in L.Addresses)
                        {
                            string k = key;
                            rec.businessIncomeStatements?.RemoveAll(x => x != null && GameStateReader.AddressKey(x.Address) == k);
                        }
                        foreach (var key in L.RealEstate)
                        {
                            string k = key;
                            rec.realEstateStatements?.RemoveAll(x => x != null && GameStateReader.AddressKey(x.Address) == k);
                        }
                        Unfold(rec, L);
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] remove: {ex.Message}"); }
            _overlaid.Clear(); _rowOwner.Clear(); _labelOwner.Clear();
        }

        /// <summary>MAIN THREAD.  Un-flip / unmerge / disconnect: take everything back out.</summary>
        public static void ClearOverlay(string why)
        {
            bool had = _overlaid.Count > 0 || _partner.Count > 0;
            RemoveAll();
            _partner.Clear(); _pending = false; _nativeKept.Clear();
            if (had) Plugin.Logger.LogInfo($"[Books] cleared ({why}).");
        }

        /// <summary>SCENE BOUNDARY ONLY (MergerFlip.Reset, after 'Game scene loaded').  M2 (review r2):
        /// clear TRACKING without touching any object, exactly as MergerFlip.Reset does for the flip set.
        /// NEVER RemoveAll() here - this runs when a DIFFERENT save is already loaded, so unfolding a
        /// stale ledger would subtract floats from, and delete rows out of, the NEWLY loaded save's
        /// records.  The ACTIVE clear while the same save is still loaded (dissolve / unmerge /
        /// disconnect) is Tick's job, and that one unfolds first.</summary>
        public static void Reset()
        {
            _overlaid.Clear(); _rowOwner.Clear(); _labelOwner.Clear(); _partner.Clear(); _nativeKept.Clear();
            _suspend = 0; _pending = false; _wasMember = false; _memberSig = ""; _edgeHeldSig = ""; _edgeRetryAt = 0f;
            _lastPublishedDay = -1; _inertLoggedDay = -1; _veilBuildLoggedDay = -1;
            LastFiledReturn = null; TaxRowTint = false; AnniversaryPending = -1;   // TAXBILL-ONE: session facts, cleared with _partner
            Plugin.Logger.LogInfo("[Books] reset: tracking cleared without touching any record (scene boundary).");
        }

        // ── B3 / D19-2: the overlay is physically LIFTED for veiled passes and for saves ─────────
        /// <summary>Lift the overlay (remove rows, subtract totals) for the duration of a veiled pass
        /// or a save.  Nesting-counted like the veil itself.</summary>
        public static void SuspendPush()
        {
            try
            {
                if (_suspend++ > 0) return;
                if (_overlaid.Count == 0) return;
                RemoveAll();
                _pending = true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] suspend: {ex.Message}"); }
        }

        public static void SuspendPop()
        {
            try
            {
                if (--_suspend > 0) return;
                if (_suspend < 0) _suspend = 0;
                if (_pending && MergerSync.IAmMember && MergerFlip.VeilDepth == 0) Apply("veil/save released");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] resume: {ex.Message}"); }
        }

        // ══ M0 — THE MEMBERSHIP EDGE (review r2, rig run T-P4-BOOKS/1) ═══════════════════
        // NOTHING published on formation: the only callers of the publish/overlay path were the display
        // surfaces (the daily popup, the weekly chart, the persona page) and the RunDaily postfix, so a
        // freshly merged pair saw nothing at all until someone opened a money screen or the day turned -
        // and the overlay was likewise never taken back on dissolve until a surface opened.  This is the
        // P3 pattern: the EDGE is an event, and a 1 Hz tick on every machine is what turns it into one.

        /// <summary>MAIN THREAD, 1 Hz from MergerFlip.Tick on EVERY machine.  Publishes and applies on
        /// the membership edge, and clears on the way out.  The edge is keyed on the group's MEMBER-SET
        /// signature, not on a bool, so a third member joining or leaving republishes too.  While the
        /// world is not settled the signature is NOT consumed - the edge re-arms and fires as soon as it
        /// is.  The display surfaces keep their own ApplyPending; this is what makes them unnecessary.</summary>
        public static void Tick()
        {
            try
            {
                if (!MergerSync.IAmMember)
                {
                    if (_memberSig.Length == 0) return;                 // already out: nothing to take back
                    _memberSig = ""; _edgeHeldSig = ""; _wasMember = false;
                    ClearOverlay("membership edge: no longer in a company");   // M2: the ACTIVE clear, while this save is still loaded
                    return;
                }

                string sig = MemberSignature();
                if (sig.Length == 0 || sig == _memberSig) return;

                if (!MPWorldReady.IsSettled)
                {   // re-arm, do not consume: the publish would be refused and the edge lost.
                    if (_edgeHeldSig != sig)
                    {
                        _edgeHeldSig = sig;
                        Plugin.Logger.LogInfo("[Books] membership edge held: the world is not settled yet — it publishes as soon as it is.");
                    }
                    return;
                }

                _wasMember = true; _pending = true;
                // r4 (re-check r3 MINOR): a refused publish re-arms, but Build allocates a full 30-day
                // bundle - retry the SAME signature at most once per 10 s (the exit condition is the
                // publish succeeding; the world-settled and session states it waits on change slowly).
                if (_edgeHeldSig == sig && UnityEngine.Time.unscaledTime < _edgeRetryAt) return;
                if (!Publish("membership edge"))
                {
                    _edgeRetryAt = UnityEngine.Time.unscaledTime + 10f;   // m-d (review r3): a REFUSED publish must not eat the edge - the signature stays
                    // unconsumed, so the next tick tries again.  The overlay still goes on once for
                    // this signature (the partners' books are already here), and the held line is
                    // bounded to one per distinct signature, exactly as the not-settled hold above is.
                    // r4b (verification): re-apply on EVERY retry, not only the first - Publish lifted the
                    // overlay just now, and a steady refusal must not leave the books blank until a screen opens.
                    Apply("membership edge");
                    try { MPPatches.RefreshTopbarMoneyChange(); } catch { }
                    if (_edgeHeldSig == sig) return;
                    _edgeHeldSig = sig;
                    Plugin.Logger.LogInfo("[Books] membership edge held: the publish was refused (its reason is logged above) - it retries in 10 s.");
                    return;
                }
                _memberSig = sig; _edgeHeldSig = "";
                Apply("membership edge");
                Plugin.Logger.LogInfo($"[Books] published (membership edge): members {sig}.");
                try { MPPatches.RefreshTopbarMoneyChange(); } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] membership tick: {ex.Message}"); }
        }

        /// <summary>The group's identity AND its member set, sorted - the thing an edge is keyed on.</summary>
        private static string MemberSignature()
        {
            try
            {
                var pids = MergerSync.MyMemberPidsOrdered;
                if (pids == null || pids.Count == 0) return "";
                var l = new List<string>(pids);
                l.Sort(StringComparer.Ordinal);
                return MergerSync.MyGroupId + "|" + string.Join(",", l);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] member signature: {ex.Message}"); return ""; }
        }

        /// <summary>B4: a display surface is about to read the list — make sure the overlay is on it.</summary>
        public static void ApplyPending(string why)
        {
            try
            {
                if (!MergerSync.IAmMember)
                {
                    if (_wasMember) { ClearOverlay("membership ended"); _wasMember = false; }
                    return;
                }
                if (!_wasMember) { _wasMember = true; _pending = true; Publish("membership edge"); }
                if (_pending && !Inert) Apply(why);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] pending ({why}): {ex.Message}"); }
        }

        /// <summary>B2: the day changed — the record for that day was REPLACED by CreateFinancialSummary,
        /// so the previous overlay for it is gone.  Called from the RunDaily postfix, which runs AFTER
        /// the persisted weekly-history write, so that history stays personal.</summary>
        public static void OnDayChanged()
        {
            try
            {
                if (!MergerSync.IAmMember) return;
                // ORDER HAZARD (review r2): Harmony does not guarantee the order of two postfixes on one
                // method, so this may run BEFORE or AFTER the authority veil's own Pop has re-applied the
                // overlay.  A bare _overlaid.Clear() is only right in the first order; an exact UNFOLD of
                // whatever the ledger says was added, against whatever records exist now, is right in both
                // (the veil's lift already emptied the ledger before the rebuild, so the early order finds
                // nothing to take back, and the late order takes back exactly what Pop just added).
                RemoveAll();
                _nativeKept.Clear();
                _pending = true;
                Publish("day change");
                Apply("day change");
                try { LogDayTable((SaveGameManager.Current?.Day ?? 0) - 1, "publish"); } catch { }   // DAYTABLE-1 C3(iii)
                try { MPPatches.RefreshTopbarMoneyChange(); } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] day change: {ex.Message}"); }
        }

        // ── D19-3: the bank loan cap must see the UN-overlaid figure ────────────────────────────
        /// <summary>PlayerHelper.CalculateDailyIncome as it would read WITHOUT the overlay: the last
        /// seven day records' totalProfit minus exactly what this machine added to each of them.</summary>
        public static float OwnOnlyDailyIncome()
        {
            float sum = 0f;
            try
            {
                var list = SaveGameManager.Current.financialSummaries;
                int from = list.Count - 7; if (from < 0) from = 0;
                for (int i = from; i < list.Count; i++)
                {
                    var rec = list[i]; if (rec == null) continue;
                    float v = rec.totalProfit;
                    if (_overlaid.TryGetValue(rec.dayNumber, out var perPid))
                        foreach (var kv in perPid) v -= kv.Value.TotalProfit;
                    sum += v;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] own-only income: {ex.Message}"); }
            return sum / 7f;
        }

        // ── D19-4: the company net worth components ─────────────────────────────────────────────
        public static void CompanyWealth(out float investments, out float loans, out float assets)
        {
            investments = loans = assets = 0f;
            try
            {
                foreach (var kv in _partner)
                {
                    if (!MergerSync.IsMemberPid(kv.Key)) continue;
                    investments += kv.Value.Investments; loans += kv.Value.LoansRemaining; assets += kv.Value.AssetsWorth;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] company wealth: {ex.Message}"); }
        }

        // ── B5 / D18: which member a row belongs to ─────────────────────────────────────────────
        public static bool TryOwnerOfAddress(string key, out string pid)
        { pid = ""; return !string.IsNullOrEmpty(key) && _rowOwner.TryGetValue(key, out pid!); }

        public static bool TryOwnerOfRowLabel(string label, out string pid)
        { pid = ""; return !string.IsNullOrEmpty(label) && _labelOwner.TryGetValue(label, out pid!); }

        private static void LearnLabel(string key, string pid)
        {
            try
            {
                var reg = RegOf(key); if (reg == null) return;
                string label = string.IsNullOrEmpty(reg.BusinessName) ? Streets.AddressHelper.ToFormattedString(reg.Address) : reg.BusinessName;
                if (!string.IsNullOrEmpty(label)) _labelOwner[label] = pid;
            }
            catch { }
        }

        // ── B9: the company tax bill ────────────────────────────────────────────────────────────
        public static float PartnerTaxDue()
        {
            float sum = 0f;
            foreach (var kv in _partner) if (MergerSync.IsMemberPid(kv.Key)) sum += kv.Value.TaxDue;
            return sum;
        }

        private static bool _oldPayloadLogged;

        /// <summary>T9 (phase 4b): the partners' CURRENT tax due only - what the tax page's one amount
        /// label natively shows (back taxes have their own label, EconoViewTaxes.cs:93).  A bundle
        /// published by a build older than this one carries TaxCurrentDue = 0, so it adds NOTHING
        /// rather than adding its whole outstanding figure to a current-only label; the fallback is
        /// logged once.</summary>
        public static float PartnerTaxCurrentDue()
        {
            float sum = 0f;
            foreach (var kv in _partner)
            {
                if (!MergerSync.IsMemberPid(kv.Key)) continue;
                if (kv.Value.TaxCurrentDue <= 0f && kv.Value.TaxDue > 0f && !_oldPayloadLogged)
                {
                    _oldPayloadLogged = true;
                    Plugin.Logger.LogInfo($"[Tax] '{kv.Key}' published before the current/back tax split - its bill adds nothing to the company total until it republishes.");
                }
                sum += kv.Value.TaxCurrentDue;
            }
            return sum;
        }

        // ══ TAXBILL-ONE — ONE COMPANY, ONE BILL (user ruling 2026-09-12) ══════════════════
        /// <summary>T1/T4.  This member's OWN sales over the last daysPerYear summaries, summed exactly
        /// as TaxHelper.PlayerShouldDoTaxes does (decompile Helpers/TaxHelper.cs:150-171 — by LIST
        /// INDEX, not by day number).  The overlay is lifted around the sum so a partner's rows are
        /// never counted; precedent TimeSync.cs:535-556.  The lift nests, so it costs nothing where it
        /// is already up (inside the RunDaily tax pass).</summary>
        public static float OwnLastYearSales()
        {
            var gi = SaveGameManager.Current;
            if (gi?.financialSummaries == null) return 0f;
            int dpy = gi.gameVariables?.daysPerYear ?? 0;
            if (dpy <= 0) return 0f;
            float sales = 0f;
            SuspendPush();
            try
            {
                var sums = gi.financialSummaries;
                int start = System.Math.Max(0, sums.Count - dpy);
                for (int i = start; i < sums.Count; i++)
                {
                    var st = sums[i]?.businessIncomeStatements;
                    if (st == null) continue;
                    foreach (var bis in st) if (bis != null) sales += bis.TotalSales;
                }
            }
            finally { SuspendPop(); }
            return sales;
        }

        /// <summary>T4: the co-members' own last-year sales, as they published them.</summary>
        public static float PartnerLastYearSales()
        {
            float sum = 0f;
            foreach (var kv in _partner) if (MergerSync.IsMemberPid(kv.Key)) sum += kv.Value.LastYearSales;
            return sum;
        }

        /// <summary>T4: co-members this machine holds NO bundle for yet.</summary>
        public static int UnpublishedMemberCount()
        {
            int n = 0;
            try
            {
                foreach (var pid in MergerSync.MyMemberPidsOrdered)
                {
                    if (string.IsNullOrEmpty(pid) || pid == MPConfig.PlayerId) continue;
                    if (!_partner.ContainsKey(pid)) n++;
                }
            }
            catch { }
            return n;
        }

        /// <summary>T3 (PATCH F): the period a pay-all should name when THIS machine has no own bill —
        /// the highest period among the co-members that still owe.</summary>
        public static int PartnerTopPaidPeriod()
        {
            int best = 0;
            foreach (var kv in _partner)
            {
                if (!MergerSync.IsMemberPid(kv.Key)) continue;
                if (kv.Value.TaxCurrentDue <= 0f) continue;
                int period = kv.Value.TaxReturn?.Day ?? kv.Value.TaxPeriod;
                if (period > best) best = period;
            }
            return best;
        }

        /// <summary>T2: a member's DISPLAY NAME as a bill row label — the only label this build adds,
        /// and no new wording (the repossession variant already draws a plain name row with an empty
        /// value, decompile TaxesMessage.cs:221).  Colour-tinted only when the renderer would let a
        /// label's markup render, which the bill patch decides before it calls CompanyReturn.
        /// GAME-PATCH-0916: it never would — AddPlainLine escapes the label (TaxesMessage.cs:366-375) —
        /// so TaxRowTint is false and this returns the plain display name.</summary>
        public static string MemberRowLabel(string pid)
        {
            string name = "";
            try { name = MPNames.Resolve(pid); } catch { }
            if (string.IsNullOrEmpty(name)) name = pid;
            if (!TaxRowTint) return name;
            if (name.IndexOf('<') >= 0 || name.IndexOf('>') >= 0) return name;   // fold b (review r1 MINOR-4): a name that could read as markup is never wrapped in it
            try
            {
                if (PlayerColours.TryColourFor(pid, out var c))
                    return "<color=#" + UnityEngine.ColorUtility.ToHtmlStringRGB(c) + ">" + name + "</color>";
            }
            catch { }
            return name;
        }

        private static float SumRows(List<(string, float)> rows)
        {
            float s = 0f;
            foreach (var r in rows) s += r.Item2;
            return s;
        }

        /// <summary>T2, MAIN THREAD.  Build the COMPANY's return from this member's own one and every
        /// co-member's published return for the SAME period.  `own` is NEVER mutated: the game hands the
        /// STORED Taxes object to the renderer on every open of the conversation (ContactsApp.cs:495-499),
        /// so a mutated argument would compound on the second open.
        ///
        /// LOSS CASE (user ruling 2026-09-12): the company total is the SUM of the members' native bills,
        /// never a netted figure — a member whose deductions exceed its income pays zero on its own
        /// machine (GenerateTaxes floors the taxable figure at 0, TaxHelper.cs:268-271), so its excess
        /// deduction must not net against a profitable member's income here.  The LOSS ADJUSTMENT row
        /// takes that excess straight back out of the deduction section, as a negative line the game's
        /// own currency formatter draws (ToCurrencyFormat = ToString("C"), GenericExtensions.cs:40-43).</summary>
        public static Taxes CompanyReturn(Taxes own, out List<string> pendingPids, out int lossRows)
        {
            pendingPids = new List<string>();
            lossRows = 0;
            var outp = new Taxes
            {
                day                = own.day,
                dueDay             = own.dueDay,
                taxPercentage      = own.taxPercentage,
                lateFeeApplied     = own.lateFeeApplied,
                businessesIncome   = new List<(string, float)>(),
                estateTaxes        = new List<(string, float)>(),
                deductibleExpenses = new List<(string, float)>(),
            };
            if (own.businessesIncome != null) outp.businessesIncome.AddRange(own.businessesIncome);
            if (own.estateTaxes != null)      outp.estateTaxes.AddRange(own.estateTaxes);

            // Deduction categories are COMPANY-wide: the same label is one summed row across members.
            // The labels are the game's own localization keys, so they stay plain (a colour tag would
            // stop TaxesMessageLine.SetPlain recognising the key, TaxesMessageLine.cs:23-30).
            var dedOrder  = new List<string>();
            var dedAmount = new Dictionary<string, float>();

            if (own.deductibleExpenses != null)
                foreach (var d in own.deductibleExpenses)
                {
                    string k = d.Item1 ?? "";
                    if (!dedAmount.ContainsKey(k)) { dedOrder.Add(k); dedAmount[k] = 0f; }
                    dedAmount[k] += d.Item2;
                }

            float gambling = own.subtotalGamblingWinnings;
            // OWN's contribution is the AS-FILED total (see LastFiledReturn): a partial payment lowers
            // the live record's totalToPay but not its subtotals.
            float total = (LastFiledReturn != null && LastFiledReturn.day == own.day)
                        ? LastFiledReturn.totalToPay : own.totalToPay;

            var losses = new List<(string, float)>();
            float ownExcess = own.subtotalDeductibleExpenses - own.subtotalRegisteredBusinesses - own.subtotalGamblingWinnings;
            if (ownExcess > 0f) losses.Add((MemberRowLabel(MPConfig.PlayerId), -ownExcess));

            foreach (var kv in _partner)
            {
                if (!MergerSync.IsMemberPid(kv.Key)) continue;
                var r = kv.Value.TaxReturn;
                if (r == null || r.Day != own.day) { pendingPids.Add(kv.Key); continue; }
                string label = MemberRowLabel(kv.Key);
                float bus = 0f;
                foreach (var row in r.Businesses) { bus += row.Amount; outp.businessesIncome.Add((label, row.Amount)); }
                foreach (var row in r.Estate)     outp.estateTaxes.Add((label, row.Amount));
                foreach (var row in r.Deductibles)
                {
                    string k = row.Label ?? "";
                    if (!dedAmount.ContainsKey(k)) { dedOrder.Add(k); dedAmount[k] = 0f; }
                    dedAmount[k] += row.Amount;
                }
                gambling += r.Gambling;
                total    += r.TotalToPay;
                float excess = r.SubtotalDeductible - bus - r.Gambling;
                if (excess > 0f) losses.Add((label, -excess));
            }

            foreach (var k in dedOrder) outp.deductibleExpenses.Add((k, dedAmount[k]));
            foreach (var l in losses) { outp.deductibleExpenses.Add(l); lossRows++; }

            outp.subtotalGamblingWinnings     = gambling;
            outp.subtotalRegisteredBusinesses = SumRows(outp.businessesIncome);
            outp.subtotalRealEstateTaxes      = SumRows(outp.estateTaxes);
            outp.subtotalDeductibleExpenses   = SumRows(outp.deductibleExpenses);
            outp.totalToPay                   = total;

            // INVARIANT: the renderer's OWN arithmetic on the object we hand it (TaxesMessage.cs:194-207
            // through GetTotalIncome :247-250, GetTaxableIncome :252-260, GetIncomeTax :262-265) must
            // land on the same total the members' bills add up to.  Warn, never throw — the bill is
            // still shown exactly as the game draws it.
            float income  = outp.subtotalRegisteredBusinesses + outp.subtotalGamblingWinnings;
            float taxable = income - outp.subtotalDeductibleExpenses;
            if (taxable < 0f) taxable = 0f;
            float drawn = taxable * outp.taxPercentage / 100f + outp.subtotalRealEstateTaxes;
            if (System.Math.Abs(drawn - outp.totalToPay) > 1f)
                Plugin.Logger.LogWarning($"[Tax] company bill arithmetic disagrees: the lines draw {drawn:F2} but the members' bills sum to {outp.totalToPay:F2} — the bill is shown as the renderer draws it.");
            return outp;
        }

        /// <summary>T4, MAIN THREAD.  A co-member's books just arrived.  If this machine declined its own
        /// anniversary assessment because the COMPANY's sales could not be totalled yet, total them now
        /// and run the game's own assessment when the company clears the $150,000 line — exactly as the
        /// join snap runs a skipped one (TimeSync.cs:534-560).</summary>
        internal static void TaxAnniversaryRecheck()
        {
            try
            {
                if (AnniversaryPending <= 0 || !MergerSync.IAmMember) return;
                var gi = SaveGameManager.Current;
                int dpy = gi?.gameVariables?.daysPerYear ?? 0;
                if (gi == null || dpy <= 0) return;
                if (gi.currentUnpaidTaxes != null || gi.Day / dpy != AnniversaryPending / dpy)
                { AnniversaryPending = -1; return; }   // a bill already exists, or the year moved on
                float company = OwnLastYearSales() + PartnerLastYearSales();
                if (company < 150000f) return;
                var m = HarmonyLib.AccessTools.Method(typeof(Helpers.TaxHelper), "ExecutePlayerTaxesEvent");
                if (m == null) { Plugin.Logger.LogWarning("[Tax] late-books assessment: TaxHelper.ExecutePlayerTaxesEvent not found."); AnniversaryPending = -1; return; }
                AnniversaryPending = -1;   // cleared BEFORE the invoke: the publish it triggers must not re-enter here
                SuspendPush();
                try { m.Invoke(null, null); }
                finally { SuspendPop(); }
                Plugin.Logger.LogInfo($"[Tax] company qualifies on the late books: assessment run now, bill dated day {gi.Day}.");
            }
            catch (Exception ex)
            {
                var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
                Plugin.Logger.LogWarning($"[Tax] late-books assessment: {inner.GetType().Name}: {inner.Message}");
            }
        }

        /// <summary>T1: the parts of a filed return, as the wire carries them (tuples do not serialize).</summary>
        private static List<CbTaxRow> ToRows(List<(string, float)>? src)
        {
            var rows = new List<CbTaxRow>();
            if (src == null) return rows;
            foreach (var r in src) rows.Add(new CbTaxRow { Label = r.Item1 ?? "", Amount = r.Item2 });
            return rows;
        }

        private static CbTaxReturn ToReturnDto(Taxes t, bool asFiled)
        {
            float taxable = t.subtotalRegisteredBusinesses + t.subtotalGamblingWinnings - t.subtotalDeductibleExpenses;
            if (taxable < 0f) taxable = 0f;
            float rebuilt = taxable * t.taxPercentage / 100f + t.subtotalRealEstateTaxes;
            return new CbTaxReturn
            {
                Day                = t.day,
                DueDay             = t.dueDay,
                Pct                = t.taxPercentage,
                TotalToPay         = asFiled ? t.totalToPay : rebuilt,
                Gambling           = t.subtotalGamblingWinnings,
                SubtotalDeductible = t.subtotalDeductibleExpenses,
                Businesses         = ToRows(t.businessesIncome),
                Estate             = ToRows(t.estateTaxes),
                Deductibles        = ToRows(t.deductibleExpenses),
            };
        }

        /// <summary>T1: the deep snapshot the filing postfix takes — the live record keeps being mutated
        /// by payments, and a ZERO bill leaves no record at all.</summary>
        public static Taxes CloneReturn(Taxes t)
        {
            return new Taxes
            {
                day                       = t.day,
                taxPercentage             = t.taxPercentage,
                dueDay                    = t.dueDay,
                lateFeeApplied            = t.lateFeeApplied,
                businessesIncome          = t.businessesIncome   == null ? new List<(string, float)>() : new List<(string, float)>(t.businessesIncome),
                estateTaxes               = t.estateTaxes        == null ? new List<(string, float)>() : new List<(string, float)>(t.estateTaxes),
                deductibleExpenses        = t.deductibleExpenses == null ? new List<(string, float)>() : new List<(string, float)>(t.deductibleExpenses),
                subtotalGamblingWinnings  = t.subtotalGamblingWinnings,
                subtotalRegisteredBusinesses = t.subtotalRegisteredBusinesses,
                subtotalRealEstateTaxes   = t.subtotalRealEstateTaxes,
                subtotalDeductibleExpenses = t.subtotalDeductibleExpenses,
                totalToPay                = t.totalToPay,
            };
        }

        private static int _payAllPendingPeriod = -1;

        /// <summary>B9-iv, MAIN THREAD.  The local player just paid their OWN bill natively; ask every
        /// partner's machine to pay theirs, so the whole company's taxes are settled.</summary>
        public static void SendPayAll(int period)
        {
            try
            {
                if (!MergerSync.IAmMember) return;            // B9-viii: inert without a merger
                if (_payAllPendingPeriod == period) { Plugin.Logger.LogInfo($"[Tax] pay-all refused: one is already pending for period {period}."); return; }
                if (PartnerTaxDue() <= 0f) { Plugin.Logger.LogInfo("[Tax] pay-all refused: no partner has an outstanding bill."); return; }
                _payAllPendingPeriod = period;
                var p = new MergerTaxPayload { PlayerId = MPConfig.PlayerId, Action = "payall", PayerPid = MPConfig.PlayerId, Period = period };
                if (MPServer.IsRunning) HostPayAll(p, MPConfig.PlayerId);
                else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerTax, MPConfig.PlayerId, p));
                else { Plugin.Logger.LogInfo("[Tax] pay-all refused: not in a session."); _payAllPendingPeriod = -1; return; }
                Plugin.Logger.LogInfo($"[Tax] pay-all sent by '{MPConfig.PlayerId}'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] pay-all: {ex.Message}"); }
        }

        /// <summary>HOST, MAIN THREAD.  Relay the pay-all to every ONLINE partner whose bill is
        /// outstanding; hold it for an offline one.</summary>
        public static void HostPayAll(MergerTaxPayload p, string senderPid)
        {
            try
            {
                if (p == null || !MergerSync.InAnyGroup(senderPid)) { Plugin.Logger.LogInfo($"[Tax] pay-all refused: '{senderPid}' is not in a company."); return; }
                int sent = 0, held = 0;
                lock (_store)
                    foreach (var kv in _store)
                    {
                        // m-b (review r3): the host's own bundle sits in this store, and
                        // MPServer.SendMergerTaxTo loops a 'payown' addressed to the host back into
                        // PayOwnForCompany - so relaying to it AND calling it directly below paid once
                        // and refused once ('already paid'), which cleared the pending period early.
                        // The host is never relayed to: the direct call below is its only path.
                        if (kv.Key == senderPid || kv.Key == MPConfig.PlayerId) continue;
                        if (!MergerSync.MergedRuntime(kv.Key, senderPid)) continue;
                        if (kv.Value.TaxDue <= 0f) continue;
                        var ask = new MergerTaxPayload { PlayerId = "host", Action = "payown", PayerPid = senderPid, Period = p.Period };
                        if (MPServer.SendMergerTaxTo(kv.Key, ask)) sent++;
                        else
                        {
                            held++;
                            lock (_held) _held[kv.Key] = new MergerTaxPayload { PlayerId = "host", Action = "payown", PayerPid = senderPid, Period = p.Period };   // M4 (r2): the send path and this one share the map
                            Plugin.Logger.LogInfo($"[Tax] pay-all held for offline '{kv.Key}'.");
                        }
                    }
                if (MPConfig.PlayerId != senderPid && MergerSync.MergedRuntime(MPConfig.PlayerId, senderPid)) { PayOwnForCompany(senderPid); sent++; }
                if (sent == 0 && held == 0) Plugin.Logger.LogInfo($"[Tax] pay-all from '{senderPid}': no partner had an outstanding bill.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] host pay-all: {ex.Message}"); }
        }

        /// <summary>HOST: pay-alls recorded for members who were offline when one was issued.  M4 (r2):
        /// cleared with the store in HostReset (session/world boundary) and always taken under its own
        /// lock - the hold site and the delivery site run from different paths.</summary>
        private static readonly Dictionary<string, MergerTaxPayload> _held = new();

        /// <summary>HOST: a member connected — deliver any pay-all held for them, but ONLY if it is
        /// still for the tax period that member is actually in.  M4 (r2): a held ask outlived its bill -
        /// the period was never re-checked, so a returning member could be told to settle a period it had
        /// already paid (or that never became its current one).</summary>
        public static void HostFlushHeld(string pid)
        {
            try
            {
                if (string.IsNullOrEmpty(pid)) return;
                MergerTaxPayload? ask;
                lock (_held) { if (!_held.TryGetValue(pid, out ask) || ask == null) return; }

                int current = -1;
                lock (_store) if (_store.TryGetValue(pid, out var bk) && bk != null) current = bk.TaxPeriod;
                if (current < 0)
                {   // (i) carried over from the 4a review: with no stored bundle the period could not be
                    // compared and the ask went out UNCHECKED - exactly the case M4 exists to stop.  The
                    // ask is KEPT instead: the membership edge makes a member publish its books within
                    // seconds of connecting, and HostStore re-runs this flush the moment it does.
                    Plugin.Logger.LogInfo($"[Tax] held pay-all for '{pid}' kept: no books bundle yet, so its tax period cannot be compared with the held period {ask.Period}.");
                    return;
                }
                if (current != ask.Period)
                {
                    lock (_held) _held.Remove(pid);
                    Plugin.Logger.LogInfo($"[Tax] held pay-all for '{pid}' DROPPED: it named period {ask.Period}, that member's current period is {current}.");
                    return;
                }

                if (MPServer.SendMergerTaxTo(pid, ask)) { lock (_held) _held.Remove(pid); Plugin.Logger.LogInfo($"[Tax] held pay-all delivered to '{pid}' (period {ask.Period})."); }
                else Plugin.Logger.LogInfo($"[Tax] held pay-all for '{pid}' kept: that member is not online here yet.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] flush held: {ex.Message}"); }
        }

        /// <summary>MAIN THREAD.  A partner asked the company to settle: run the GAME'S OWN pay action
        /// for this machine's own bill.  The money leaves the shared wallet through ChangeMoney, which
        /// the MergerWallet mirror already follows.</summary>
        public static void PayOwnForCompany(string askedBy)
        {
            bool ok = false; string reason = "";
            try
            {
                if (!MergerSync.IAmMember) { reason = "not in a company"; }
                else if (!Helpers.TaxHelper.HasAnyTaxesToPay()) { reason = "already paid"; }
                else
                {
                    _payingForCompany = true;
                    try
                    {
                        bool a = Helpers.TaxHelper.PayCurrentTaxes();
                        bool b = Helpers.TaxHelper.PayBackTaxes();
                        ok = a && b;
                        if (!ok) reason = "the game refused the payment (insufficient balance at that instant)";
                    }
                    finally { _payingForCompany = false; }
                }
            }
            catch (Exception ex) { reason = ex.Message; }

            if (ok) Plugin.Logger.LogInfo($"[Tax] paid own bill on behalf of the company (asked by '{askedBy}').");
            else    Plugin.Logger.LogWarning($"[Tax] partner '{MPConfig.PlayerId}' pay refused: {reason}.");

            try
            {
                var rep = new MergerTaxPayload { PlayerId = MPConfig.PlayerId, Action = "report", PayerPid = askedBy, Ok = ok, Reason = reason };
                if (MPServer.IsRunning) HostReport(rep, MPConfig.PlayerId);
                else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerTax, MPConfig.PlayerId, rep));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] report back: {ex.Message}"); }
            Publish("tax paid");
        }

        /// <summary>TRUE while PayOwnForCompany runs the native pay action, so the pay hook does not
        /// fire a second pay-all off the relayed payment.</summary>
        private static bool _payingForCompany;
        public static bool PayingForCompany => _payingForCompany;

        /// <summary>HOST: relay one partner's result back to the payer's log.</summary>
        public static void HostReport(MergerTaxPayload rep, string senderPid)
        {
            try
            {
                if (rep == null) return;
                if (string.IsNullOrEmpty(rep.PayerPid) || rep.PayerPid == MPConfig.PlayerId) { LogReport(rep, senderPid); return; }
                if (!MPServer.SendMergerTaxTo(rep.PayerPid, rep)) LogReport(rep, senderPid);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] host report: {ex.Message}"); }
        }

        public static void LogReport(MergerTaxPayload rep, string senderPid)
        {
            if (rep == null) return;
            _payAllPendingPeriod = -1;
            if (rep.Ok) Plugin.Logger.LogInfo($"[Tax] partner '{senderPid}' paid its own bill on behalf of the company.");
            else        Plugin.Logger.LogWarning($"[Tax] partner '{senderPid}' pay refused: {rep.Reason}.");
        }

        // ── B6: testdrive ───────────────────────────────────────────────────────────────────────
        public static string TestDriveLine(string addressKey)
        {
            try
            {
                if (!string.IsNullOrEmpty(addressKey))
                {
                    var gi = SaveGameManager.Current;
                    if (!TryAddressOf(addressKey, out _)) return $"ERR books: no building for '{addressKey}'";
                    FinancialSummary.BusinessIncomeStatement? best = null; int bestDay = -1;
                    foreach (var rec in gi.financialSummaries)
                    {
                        if (rec?.businessIncomeStatements == null || rec.dayNumber < bestDay) continue;
                        foreach (var b in rec.businessIncomeStatements)
                            if (b != null && GameStateReader.AddressKey(b.Address) == addressKey && rec.dayNumber > bestDay) { best = b; bestDay = rec.dayNumber; }
                    }
                    if (best == null) return $"OK books addr={addressKey} no statement";
                    string src = TryOwnerOfAddress(addressKey, out var pid) ? $"partner:{pid}" : "own";
                    return $"OK books addr={addressKey} day={bestDay} sales={best.TotalSales:F2} profit={best.TotalProfit:F2} source={src}";
                }

                int addrs = 0; float profit = 0f; int lastDay = -1;
                foreach (var dayKv in _overlaid)
                {
                    if (dayKv.Key > lastDay) lastDay = dayKv.Key;
                    foreach (var pidKv in dayKv.Value) { addrs += pidKv.Value.Addresses.Count + pidKv.Value.RealEstate.Count; profit += pidKv.Value.TotalProfit; }
                }
                return $"OK books days={_overlaid.Count} addrs={addrs} lastDay={lastDay} profitAdded={profit:F2}"
                     + $" partners={_partner.Count} inert={(Inert ? 1 : 0)} taxPartners={PartnerTaxDue():F2}";
            }
            catch (Exception ex) { return $"ERR books: {ex.Message}"; }
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────
        // ── DAYTABLE-1 (log-only) ──────────────────────────────────────────
        /// <summary>C1 budget: silent fold drops reported (max 60 per session).</summary>
        private static int _foldDropLines;
        /// <summary>C3 budgets: DAYTABLE lines emitted, overall and from the fold path.</summary>
        private static int _dayTableLines, _dayTableFoldLines;

        /// <summary>C2: ONE line describing a day record end to end.  own{} is the record MINUS every
        /// float this machine folded into it (the removal ledger is the exact record of that), folded{}
        /// is what each partner's fold added, recordTotal is rec.totalProfit as it stands right now —
        /// which is the number the end-of-day popup reads (DailySummary.cs:70-79).  Read-only: it
        /// touches no record and no ledger.  Field names are FinancialSummary's own
        /// (decompile Entities/FinancialSummary.cs:61-89).</summary>
        internal static string DayTableLine(int day, string why)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                string t = "-";
                try { t = $"D{TimeHelper.CurrentDay} {TimeHelper.CurrentHour:00}:{(int)TimeHelper.CurrentMinute:00}"; } catch { }
                sb.Append("[Books] DAYTABLE day=").Append(day).Append(" why=").Append(why)
                  .Append(" me=").Append(MPConfig.PlayerId).Append(" t=").Append(t);

                var rec = SaveGameManager.Current?.financialSummaries?.Find(x => x != null && x.dayNumber == day);
                if (rec == null) { sb.Append(" own{NO LOCAL RECORD} folded{} recordTotal=-"); return sb.ToString(); }

                float fBP = 0f, fLoan = 0f, fHI = 0f, fHH = 0f, fRE = 0f, fNI = 0f, fPF = 0f, fSI = 0f, fRes = 0f, fUW = 0f, fTP = 0f;
                var folded = new System.Text.StringBuilder();
                if (_overlaid.TryGetValue(day, out var perPid))
                    foreach (var kv in perPid)
                    {
                        var L = kv.Value;
                        fBP += L.BusinessProfit; fLoan += L.LoanExpenses; fHI += L.HealthInsurance;
                        fHH += L.HeadhunterFees; fRE += L.RealEstateTotal; fNI += L.NegativeInterest;
                        fPF += L.ParkingFees;    fSI += L.SalaryIncome;   fRes += L.ResidentialExpenses;
                        fUW += L.UnassignedWages; fTP += L.TotalProfit;
                        if (folded.Length > 0) folded.Append("; ");
                        folded.Append(kv.Key).Append(":{total=").Append(L.TotalProfit.ToString("F2"))
                              .Append(" business=").Append(L.BusinessProfit.ToString("F2"))
                              .Append(" loan=").Append(L.LoanExpenses.ToString("F2"))
                              .Append(" healthIns=").Append(L.HealthInsurance.ToString("F2"))
                              .Append(" headhunter=").Append(L.HeadhunterFees.ToString("F2"))
                              .Append(" realEstate=").Append(L.RealEstateTotal.ToString("F2"))
                              .Append(" negInterest=").Append(L.NegativeInterest.ToString("F2"))
                              .Append(" parking=").Append(L.ParkingFees.ToString("F2"))
                              .Append(" salary=").Append(L.SalaryIncome.ToString("F2"))
                              .Append(" residential=").Append(L.ResidentialExpenses.ToString("F2"))
                              .Append(" unassignedWages=").Append(L.UnassignedWages.ToString("F2"))
                              .Append(" rows=").Append(L.Addresses.Count).Append("+").Append(L.RealEstate.Count)
                              .Append("}");
                    }

                sb.Append(" own{total=").Append((rec.totalProfit - fTP).ToString("F2"))
                  .Append(" salaryIncome=").Append((rec.salaryIncome - fSI).ToString("F2"))
                  .Append(" totalBusinessProfit=").Append((rec.totalBusinessProfit - fBP).ToString("F2"))
                  .Append(" totalLoanExpenses=").Append((rec.totalLoanExpenses - fLoan).ToString("F2"))
                  .Append(" totalHealthInsuranceExpenses=").Append((rec.totalHealthInsuranceExpenses - fHI).ToString("F2"))
                  .Append(" totalHeadhunterReplacementFees=").Append((rec.totalHeadhunterReplacementFees - fHH).ToString("F2"))
                  .Append(" totalRealEstate=").Append((rec.totalRealEstate - fRE).ToString("F2"))
                  .Append(" negativeInterestRates=").Append((rec.negativeInterestRates - fNI).ToString("F2"))
                  .Append(" parkingFees=").Append((rec.parkingFees - fPF).ToString("F2"))
                  .Append(" totalResidentialExpenses=").Append((rec.totalResidentialExpenses - fRes).ToString("F2"))
                  .Append(" totalUnassignedStaffWages=").Append((rec.totalUnassignedStaffWages - fUW).ToString("F2"))
                  .Append(" bizRows=").Append(rec.businessIncomeStatements?.Count ?? 0)
                  .Append(" reRows=").Append(rec.realEstateStatements?.Count ?? 0)
                  .Append(" resRows=").Append(rec.residentialStatements?.Count ?? 0)
                  .Append("}");
                sb.Append(" folded{").Append(folded).Append("}");
                sb.Append(" recordTotal=").Append(rec.totalProfit.ToString("F2"));
            }
            catch (Exception ex) { sb.Append(" [DAYTABLE failed: ").Append(ex.Message).Append("]"); }
            return sb.ToString();
        }

        /// <summary>C3: budgeted emitter — merged sessions only (the same membership test every other
        /// books path uses).  Log-only; no behaviour anywhere depends on it.</summary>
        internal static void LogDayTable(int day, string why)
        {
            try
            {
                if (!MergerSync.IAmMember) return;
                if (_dayTableLines >= 300) return;
                if (why == "fold") { if (_dayTableFoldLines >= 120) return; _dayTableFoldLines++; }
                _dayTableLines++;
                Plugin.Logger.LogInfo(DayTableLine(day, why));
            }
            catch { }
        }

        private static Ledger LedgerOf(int day, string pid)
        {
            if (!_overlaid.TryGetValue(day, out var perPid)) { perPid = new Dictionary<string, Ledger>(); _overlaid[day] = perPid; }
            if (!perPid.TryGetValue(pid, out var L)) { L = new Ledger(); perPid[pid] = L; }
            return L;
        }

        private static bool IsOwnHere(string key)
        {
            try { if (MergerAbsence.SimulatesHere(key)) return true; } catch { }
            var reg = RegOf(key);
            return reg != null && MergerFlip.TrulyMine(reg);
        }

        private static BuildingRegistration? RegOf(string key)
        {
            try
            {
                foreach (var reg in SaveGameManager.Current.BuildingRegistrations)
                    if (reg != null && GameStateReader.AddressKey(reg) == key) return reg;
            }
            catch { }
            return null;
        }

        private static bool TryAddressOf(string key, out Address addr)
        {
            var r = RegOf(key);
            if (r == null) { addr = default!; return false; }
            addr = r.Address; return true;
        }

        private static FinancialSummary.BusinessIncomeStatement FromDto(CbStatement d, Address addr)
        {
            var b = new FinancialSummary.BusinessIncomeStatement
            {
                Address = addr,
                Sales = new List<FinancialSummary.BusinessIncomeStatement.TransactionGroupEntry>(),
                Resources = new List<FinancialSummary.BusinessIncomeStatement.TransactionGroupEntry>(),
                SalaryExpenses = d.SalaryExpenses, RentExpenses = d.RentExpenses,
                MarketingExpenses = d.MarketingExpenses, Theft = d.Theft, LicensingFees = d.LicensingFees,
                TotalSales = d.TotalSales, TotalResources = d.TotalResources,
                TotalOngoing = d.TotalOngoing, TotalProfit = d.TotalProfit,
            };
            foreach (var e in d.Sales) b.Sales.Add(new FinancialSummary.BusinessIncomeStatement.TransactionGroupEntry { ItemName = e.ItemName, Amount = e.Amount });
            foreach (var e in d.Resources) b.Resources.Add(new FinancialSummary.BusinessIncomeStatement.TransactionGroupEntry { ItemName = e.ItemName, Amount = e.Amount });
            return b;
        }
    }
}
