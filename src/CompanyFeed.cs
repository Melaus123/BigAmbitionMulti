using Entities;

namespace BigAmbitionsMP
{
    /// <summary>
    /// MERGER PHASE 4b — SHARED TRANSACTION FEED (D19-5, user-decided 2026-09-11; RESTRUCTURED r3).
    ///
    /// Every member's money movements are shown to every member.  A member's own machine already
    /// records each of its money changes as a native Transaction (GameManager.ChangeMoney :1136-1146
    /// builds one and Enqueues it), so this file SHARES that record with the co-members — and the
    /// co-members show it as a ROW ON THE SCREEN, built at the moment the list is built.
    ///
    /// WHY NOT THE QUEUE (r2 rig run T-P4-FEED run 2, build 23).  r1/r2 appended a display COPY of a
    /// partner's record to gi.Transactions.  That is dead: the game's queue normally SITS at or above
    /// its 1000-entry cap, because GameManager.ReduceTransactionQueue (GameManager.cs:1156-1161) only
    /// dequeues entries that are BOTH beyond 1000 AND older than 6 days — so a fixture host with 1176
    /// own entries had no room at all, and a member with fewer would have paid for a partner's copy
    /// with its own history.  gi.Transactions is also SUMMED (FinancialSummaryHelper :58 and :150, one
    /// of them keying on the transaction TYPE alone at :64) and PERSISTED into the member's .hsg.
    ///
    /// THE RESTRUCTURE: nothing is ever enqueued.  A received entry lives in a MOD-SIDE REGISTRY
    /// (_partner, a per-owner ring), and the two transaction screens get their partner rows from a
    /// postfix that merges freshly-built (never-enqueued) Transaction objects into the controller's
    /// OWN model list.  Because the game's queue is never touched, there is nothing to lift for a
    /// veiled pass, nothing to strip for a save, and no budget to police: the summary passes, the tax
    /// page and every .hsg see this member's own transactions and nothing else, by construction.
    ///
    /// INERTNESS (T8): nothing runs without a merger — a non-member's capture and both screen
    /// postfixes return on one IAmMember read, with no allocation.  Every refusal logs why.
    /// </summary>
    public static class CompanyFeed
    {
        /// <summary>T2: how much of ONE partner's history this machine keeps (oldest dropped first).
        /// A display ring only — it costs the member's own transaction queue nothing.</summary>
        public const int MaxPartnerPerOwner = 200;

        /// <summary>T1: entries the HOST keeps per owner in memory for a join replay.  NOT in the
        /// manifest: the feed is a display convenience, and a member's own transactions are in its own
        /// save — a session that restarts re-fills it from the members' next money movements.</summary>
        public const int HostHistoryPerOwner = 200;

        // ── member state ────────────────────────────────────────────────────────────────────────
        /// <summary>ownerPid → that owner's received entries, oldest first.  THE registry: these are
        /// DTOs, not game objects, and no path puts any of them into gi.Transactions.</summary>
        private static readonly Dictionary<string, List<PwTransaction>> _partner = new();

        /// <summary>R9 (r2 re-check): ownerPid → the highest Seq admitted from that owner.  The join /
        /// reconnect replay (MPServer.SendJoinReplayTo → SendCompanyFeedTo, reached from BOTH the
        /// Welcome path and the late-join path) re-sends that owner's whole ring, so without this every
        /// partner row appeared twice after a reconnect.  One owner's Seq is a monotonic counter on
        /// that owner's machine, so `Seq &lt;= high water` is an exact duplicate test and O(1).</summary>
        /// <remarks>M1 (r4 re-check) — THE MARK MUST NOT OUTLIVE THE OWNER'S SESSION.  _seq restarts at
        /// 0 when the OWNER's PROCESS restarts, but this mark does not go with it: a departed member
        /// stays in MemberPids (MPServer.cs:6836-6843 is not filtered by online, and PidOfStable
        /// :5960-5966 resolves through the never-pruned StableIdByPlayer), so MemberSignature is
        /// unchanged and DropNonMembers never fires.  The returning member's Seq 1..N would all be read
        /// as duplicates.  SessionNonce is what separates the two runs.</remarks>
        private static readonly Dictionary<string, long> _maxSeq = new();

        /// <summary>M1: ownerPid → the SessionNonce this machine currently holds that owner's ring
        /// under.  A different nonce means that owner's process restarted, so its Seq counter restarted
        /// too: that owner's ring AND its high-water mark are thrown away and the new nonce adopted.
        /// A pre-r4 sender's entry carries "" — one nonce, so it behaves exactly as before.</summary>
        private static readonly Dictionary<string, string> _nonceOf = new();

        /// <summary>EconoView row model → owner pid, for the row tint (T5).</summary>
        private static readonly Dictionary<object, string> _modelOwner = new();

        /// <summary>R5: the last-transactions controller a postfix saw, so a receipt can refresh a list
        /// that is OPEN right now.  A Unity object: `== null` also covers a destroyed one.</summary>
        private static UI.Smartphone.Apps.EconoView.EconoViewLastTransactionsScrollerController? _openLast;

        private static long   _seq;
        private static string _memberSig = "";

        /// <summary>M1: THIS PROCESS's session nonce, minted once at static init and stamped on every
        /// entry this machine captures.  It changes exactly when _seq restarts, which is the only thing
        /// it has to track.</summary>
        private static readonly string _nonce = System.Guid.NewGuid().ToString("N");

        // m4: the full-transactions page's three dropdown selections, stashed by a PREFIX on
        // EconoViewFullTransactions.RefreshTransactionsList just before it calls the controller's Load.
        private static int    _fDay    = -1;                 // -1 = all days
        private static string _fType   = "";                 // "" = all types
        private static int    _fAmount;                      // Transaction.AmountOption: 0 All, 1 Positive, 2 Negative

        public static int PartnerCount
        { get { int n = 0; foreach (var kv in _partner) n += kv.Value.Count; return n; } }

        public static int OwnerCount => _partner.Count;

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // T1 — SHARE (this member's own transactions)
        // ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>MAIN THREAD.  The record of the money change the native ChangeMoney has JUST
        /// enqueued, as a DTO for the wallet forward.  NULL = nothing to share (not merged, or no
        /// queue).  There is NO "is this one a partner's?" guard any more and none is needed: after the
        /// r3 restructure no path anywhere puts a partner-built Transaction into gi.Transactions, so
        /// every entry in that queue is this machine's own by construction.</summary>
        public static PwTransaction? CaptureOwn()
        {
            try
            {
                if (!MergerSync.IAmMember) return null;               // T8: inert without a merger
                var q = SaveGameManager.Current?.Transactions;
                if (q == null || q.Count == 0) return null;
                // m6 (review r2): q.Last() walks the queue (~1000 references) once per money change.
                // No cheaper way in exists and it is LEFT as is: Queue<T> exposes no back accessor,
                // LINQ's Last() has no fast path for it (Queue<T> is not an IList), and a prefix's
                // __state cannot carry an object the native method only creates afterwards.  The only
                // O(1) alternative is reflecting into Queue<T>'s private _array/_head, which is not
                // worth a pointer walk.
                var t = q.Last();                                     // a Queue enumerates front→back: the last is the one just enqueued
                var rec = ToDto(t);
                Plugin.Logger.LogInfo($"[Feed] forwarded {rec.Type} {rec.Amount:F2}");
                return rec;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] capture: {ex.Message}"); return null; }
        }

        private static PwTransaction ToDto(Transaction t)
        {
            var rec = new PwTransaction
            {
                OwnerPid   = MPConfig.PlayerId,
                Seq        = ++_seq,
                SessionNonce = _nonce,                        // M1: which run of _seq this Seq belongs to
                Type       = t.transactionType ?? "",
                Amount     = t.amount,
                Balance    = t.balance,
                Deductible = t.isTaxDeductible,
            };
            if (t.transactionCategories != null)
                foreach (var c in t.transactionCategories) if (!string.IsNullOrEmpty(c)) rec.Categories.Add(c);
            if (t.transactionData != null)
                foreach (var kv in t.transactionData) rec.Data[kv.Key] = kv.Value;
            // Timestamp.Minute is a FLOAT on this build (Day/Hour are ints) - the cast is required.
            try { if (t.timestamp != null) { rec.Day = t.timestamp.Day; rec.Hour = t.timestamp.Hour; rec.Minute = (int)t.timestamp.Minute; } } catch { }
            try { rec.AddressKey = GameStateReader.AddressKey(t.address) ?? ""; } catch { }
            return rec;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // T1 — HOST: relay to the sender's co-members + the join-replay ring
        // ═══════════════════════════════════════════════════════════════════════════════════════

        private static readonly Dictionary<string, List<PwTransaction>> _hostRing = new();

        /// <summary>M1, HOST end: ownerPid → the SessionNonce that owner's ring is held under.</summary>
        private static readonly Dictionary<string, string> _hostNonce = new();

        public static void HostReset()
        {
            lock (_hostRing) { _hostRing.Clear(); _hostNonce.Clear(); }
            Plugin.Logger.LogInfo("[Feed] host ring reset (world/session boundary).");
        }

        /// <summary>HOST, MAIN THREAD.  One member's transaction arrived (riding its wallet forward):
        /// keep the last N for a join replay and fan it out to that member's ONLINE co-members.</summary>
        public static void HostIngest(PwTransaction? rec, string senderPid)
        {
            try
            {
                if (rec == null || string.IsNullOrEmpty(senderPid)) return;
                if (rec.OwnerPid != senderPid)
                { Plugin.Logger.LogWarning($"[Feed] ingest refused: '{senderPid}' sent an entry owned by '{rec.OwnerPid}'."); return; }
                if (!MergerSync.InAnyGroup(senderPid))
                { Plugin.Logger.LogInfo($"[Feed] ingest refused: '{senderPid}' is not in a company."); return; }

                lock (_hostRing)
                {
                    // M1: a sender whose nonce changed has restarted its process, so its Seq restarted
                    // too.  What the ring still holds belongs to a run that is over: keeping it would
                    // make the ring hold TWO Seq runs, and the next joiner's high-water test would drop
                    // the whole second run as duplicates.
                    string nonce = rec.SessionNonce ?? "";
                    if (_hostNonce.TryGetValue(senderPid, out var knownNonce) && knownNonce != nonce)
                    {
                        int lost = _hostRing.TryGetValue(senderPid, out var oldRing) ? oldRing.Count : 0;
                        _hostRing.Remove(senderPid);
                        Plugin.Logger.LogInfo($"[Feed] '{senderPid}' started a new session - its ring restarts ({lost} entries dropped).");
                    }
                    _hostNonce[senderPid] = nonce;

                    if (!_hostRing.TryGetValue(senderPid, out var ring)) { ring = new List<PwTransaction>(); _hostRing[senderPid] = ring; }
                    ring.Add(rec);
                    while (ring.Count > HostHistoryPerOwner) ring.RemoveAt(0);
                }
                MPServer.SendCompanyFeedToGroup(rec);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] host ingest: {ex.Message}"); }
        }

        /// <summary>HOST: every stored entry a JOINER is entitled to (its co-members', never its own).</summary>
        public static List<PwTransaction> HostReplayFor(string joinerPid)
        {
            var outp = new List<PwTransaction>();
            try
            {
                if (string.IsNullOrEmpty(joinerPid)) return outp;
                lock (_hostRing)
                    foreach (var kv in _hostRing)
                    {
                        if (kv.Key == joinerPid) continue;
                        if (!MergerSync.MergedRuntime(kv.Key, joinerPid)) continue;
                        outp.AddRange(kv.Value);
                    }
                // m3 (review r2): List.Sort is UNSTABLE and a stamp is only minute-resolution - one
                // owner's Seq is what keeps its own entries in the order that owner enqueued them.
                outp.Sort((a, b) =>
                {
                    int c = Stamp(a).CompareTo(Stamp(b));
                    if (c != 0) return c;
                    c = string.CompareOrdinal(a.OwnerPid, b.OwnerPid);
                    return c != 0 ? c : a.Seq.CompareTo(b.Seq);
                });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] join replay for '{joinerPid}': {ex.Message}"); }
            return outp;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // R1 — THE REGISTRY (a partner's entry is kept, never enqueued)
        // ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>MAIN THREAD.  A relay or a join replay arrived.</summary>
        public static void Receive(CompanyFeedPayload p)
        {
            try
            {
                if (p == null || p.Entries == null || p.Entries.Count == 0)
                { Plugin.Logger.LogInfo("[Feed] receipt refused: empty payload."); return; }
                if (!MergerSync.IAmMember)
                { Plugin.Logger.LogInfo("[Feed] receipt refused: this machine is not in a merged company."); return; }

                int dup = 0, added = 0;
                var perOwner = new Dictionary<string, int>();
                foreach (var e in p.Entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.OwnerPid)) continue;
                    if (e.OwnerPid == MPConfig.PlayerId)
                    { Plugin.Logger.LogInfo("[Feed] receipt refused: this machine's OWN entry echoed back."); continue; }
                    if (!MergerSync.IsMemberPid(e.OwnerPid))
                    { Plugin.Logger.LogInfo($"[Feed] receipt refused: '{e.OwnerPid}' is not in this machine's company."); continue; }
                    // M1: the high-water mark is only meaningful INSIDE one run of that owner's Seq
                    // counter.  A different nonce = that owner's process restarted, so everything held
                    // for it belongs to a finished run: drop its ring and its mark, adopt the nonce.
                    // Without this the returning member's Seq 1..N are all read as duplicates and none
                    // of its new rows is ever shown.
                    string nonce = e.SessionNonce ?? "";
                    if (_nonceOf.TryGetValue(e.OwnerPid, out var knownNonce))
                    {
                        if (knownNonce != nonce)
                        {
                            int lost = _partner.TryGetValue(e.OwnerPid, out var oldRing) ? oldRing.Count : 0;
                            _partner.Remove(e.OwnerPid); _maxSeq.Remove(e.OwnerPid); _nonceOf[e.OwnerPid] = nonce;
                            _modelOwner.Clear();                  // the dropped rows' models must not keep a tint
                            Plugin.Logger.LogInfo($"[Feed] '{e.OwnerPid}' started a new session - its ring restarts ({lost} rows dropped).");
                        }
                    }
                    else _nonceOf[e.OwnerPid] = nonce;

                    // R9: the replay re-sends the owner's whole ring - anything at or below the high
                    // water mark is already held (or was held and aged out) and must not land twice.
                    if (_maxSeq.TryGetValue(e.OwnerPid, out var hi) && e.Seq <= hi) { dup++; continue; }
                    _maxSeq[e.OwnerPid] = e.Seq;

                    if (!_partner.TryGetValue(e.OwnerPid, out var ring)) { ring = new List<PwTransaction>(); _partner[e.OwnerPid] = ring; }
                    ring.Add(e);
                    while (ring.Count > MaxPartnerPerOwner) ring.RemoveAt(0);
                    perOwner[e.OwnerPid] = (perOwner.TryGetValue(e.OwnerPid, out var n) ? n : 0) + 1;
                    added++;
                }

                if (dup > 0) Plugin.Logger.LogInfo($"[Feed] replay: {dup} duplicates dropped.");
                foreach (var kv in perOwner)
                    Plugin.Logger.LogInfo($"[Feed] received {kv.Value} entries of '{kv.Key}'.");
                if (added > 0) RefreshOpenList("a receipt");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] receive: {ex.Message}"); }
        }

        /// <summary>The display row for a partner's record: the game's own Transaction, built through
        /// the game's own TransactionInfo so every label path reads it exactly as a native one.  It is
        /// NEVER enqueued — it exists only for as long as the screen's model list does.</summary>
        private static Transaction? FromDto(PwTransaction e)
        {
            try
            {
                var data = new Dictionary<string, string>();
                if (e.Data != null) foreach (var kv in e.Data) data[kv.Key] = kv.Value;
                var t = new Transaction(new TransactionInfo(e.Type ?? "", data, e.Deductible))
                {
                    amount  = e.Amount,
                    balance = e.Balance,
                };
                if (e.Categories != null && e.Categories.Count > 0) t.transactionCategories = new List<string>(e.Categories);
                if (t.transactionData == null) t.transactionData = data;
                try { if (TryAddressOf(e.AddressKey, out var addr)) t.address = addr; } catch { }
                try { if (t.timestamp != null) { t.timestamp.Day = e.Day; t.timestamp.Hour = e.Hour; t.timestamp.Minute = e.Minute; } } catch { }
                return t;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] row refused: {ex.Message}"); return null; }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // R2/R3 — THE SCREEN OVERLAY (the partner rows are produced where the list is built)
        // ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>MAIN THREAD, from a transactions-screen postfix.  Build one model per registry entry
        /// and merge them into the controller's own model list, which BOTH screens keep in DAY-DESCENDING
        /// order (EconoViewLastTransactionsScrollerController.SortByDayDescending :48-61, a stable
        /// insertion sort; EconoViewFullTransactions.RefreshTransactionsList :149-151, `orderby
        /// x.timestamp.Day descending` over the reversed queue).  Neither screen keeps only the last N —
        /// LoadLatest adds EVERY queue entry (:35-38) and Load takes the whole filtered sequence (:13) —
        /// so the size rule after the merge is the same one: keep them all, the per-owner ring is what
        /// bounds the partner side.  The merge is STABLE: every own row keeps its exact position
        /// relative to every other own row.  Returns how many partner rows went in.
        /// m4 (r4): `filtered` = the caller is the FULL page, whose day/type/amount dropdowns have
        /// already narrowed the OWN rows before Load was reached — the partner rows are put through the
        /// same three tests (PassesFullPageFilter) so the page filters what it shows, not just half of
        /// it.  The last-transactions list has no filters and passes false.</summary>
        public static int MergeInto<TModel>(List<TModel> data, Func<Transaction, TModel?> makeModel,
                                            Func<TModel, int> dayOf, string screen,
                                            bool filtered = false) where TModel : class
        {
            try
            {
                if (data == null || !MergerSync.IAmMember || _partner.Count == 0) return 0;   // R8: inert

                // Newest first, to match the list we are merging into.
                var flat = new List<PwTransaction>();
                foreach (var kv in _partner) flat.AddRange(kv.Value);
                flat.Sort((a, b) =>
                {
                    int c = Stamp(b).CompareTo(Stamp(a));
                    if (c != 0) return c;
                    c = string.CompareOrdinal(a.OwnerPid, b.OwnerPid);
                    return c != 0 ? c : b.Seq.CompareTo(a.Seq);
                });

                if (filtered)
                {
                    var keep = new List<PwTransaction>(flat.Count);
                    int hidden = 0;
                    foreach (var e in flat) { if (PassesFullPageFilter(e)) keep.Add(e); else hidden++; }
                    if (hidden > 0) Plugin.Logger.LogInfo($"[Feed] {hidden} partner rows hidden by the page's day/type/amount selection.");
                    flat = keep;
                    if (flat.Count == 0) return 0;
                }

                var rows    = new List<TModel>(flat.Count);
                var rowDays = new List<int>(flat.Count);
                foreach (var e in flat)
                {
                    var t = FromDto(e);
                    if (t == null) continue;
                    TModel? m;
                    try { m = makeModel(t); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] row model refused: {ex.Message}"); continue; }
                    if (m == null) continue;
                    rows.Add(m); rowDays.Add(e.Day);
                    _modelOwner[m] = e.OwnerPid;                 // T5: whose row this is, for the tint
                }
                if (rows.Count == 0) return 0;

                var merged = new List<TModel>(data.Count + rows.Count);
                int i = 0;
                foreach (var own in data)
                {
                    int d;
                    try { d = own == null ? int.MinValue : dayOf(own); } catch { d = int.MinValue; }
                    while (i < rows.Count && rowDays[i] > d) merged.Add(rows[i++]);
                    merged.Add(own);
                }
                while (i < rows.Count) merged.Add(rows[i++]);
                data.Clear();
                data.AddRange(merged);                            // in place: the controller's own field

                Plugin.Logger.LogInfo($"[Feed] shown {rows.Count} partner rows ({screen}).");
                return rows.Count;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] merge ({screen}): {ex.Message}"); return 0; }
        }

        /// <summary>m4, MAIN THREAD, from the PREFIX on EconoViewFullTransactions.RefreshTransactionsList
        /// (which runs BEFORE that method calls the controller's Load, so the Load postfix sees the
        /// selection of the refresh in flight).  `amountOption` is Transaction.AmountOption as an int.
        /// Three stores, no allocation: it costs a non-member nothing worth guarding.</summary>
        public static void NoteFullPageFilter(int day, string type, int amountOption)
        {
            _fDay = day; _fType = type ?? ""; _fAmount = amountOption;
        }

        /// <summary>m4: the page's own filter, mirrored EXACTLY (EconoViewFullTransactions.cs:129-142):
        /// day when a day is selected (-1 = all), then type when a type is selected ("" = all), then
        /// AmountOption.Positive = `amount &gt; 0f` / .Negative = `amount &lt; 0f` (.All = no test).</summary>
        private static bool PassesFullPageFilter(PwTransaction e)
        {
            if (_fDay != -1 && e.Day != _fDay) return false;
            if (!string.IsNullOrEmpty(_fType) && (e.Type ?? "") != _fType) return false;
            if (_fAmount == 1 && !(e.Amount > 0f)) return false;
            if (_fAmount == 2 && !(e.Amount < 0f)) return false;
            return true;
        }

        /// <summary>R5: the last-transactions controller that just built a list, so a receipt or a clear
        /// can refresh it while it is on screen.</summary>
        public static void NoteOpenList(UI.Smartphone.Apps.EconoView.EconoViewLastTransactionsScrollerController c)
            => _openLast = c;

        /// <summary>R5: rebuild an OPEN last-transactions list exactly as the game does.  Nothing happens
        /// if the controller is gone or off screen — the next open rebuilds it anyway.  The full
        /// transactions page rebuilds on its own next open or filter change.</summary>
        private static void RefreshOpenList(string why)
        {
            try
            {
                var c = _openLast;
                if (c == null) return;                    // Unity's == also covers a destroyed object
                if (!c.isActiveAndEnabled) return;
                c.LoadLatest();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] refresh ({why}): {ex.Message}"); }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // R5 — Clear / membership
        // ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>MAIN THREAD.  Un-flip / unmerge / disconnect: the registry goes, and with it every
        /// partner row the next list build would have produced.  Nothing in the game is touched.</summary>
        public static void ClearAll(string why)
        {
            try
            {
                bool had = _partner.Count > 0;
                _partner.Clear(); _maxSeq.Clear(); _nonceOf.Clear(); _modelOwner.Clear();
                if (had) { Plugin.Logger.LogInfo($"[Feed] cleared ({why})."); RefreshOpenList(why); }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] clear ({why}): {ex.Message}"); }
        }

        /// <summary>One owner left this machine's company: its rows go, nobody else's.</summary>
        private static void DropNonMembers()
        {
            var gone = new List<string>();
            foreach (var kv in _partner) if (!MergerSync.IsMemberPid(kv.Key)) gone.Add(kv.Key);
            if (gone.Count == 0) return;
            int n = 0;
            foreach (var pid in gone) { n += _partner[pid].Count; _partner.Remove(pid); _maxSeq.Remove(pid); _nonceOf.Remove(pid); }
            _modelOwner.Clear();
            Plugin.Logger.LogInfo($"[Feed] cleared ({n} rows of {gone.Count} departed owner(s)).");
            RefreshOpenList("a departed owner");
        }

        /// <summary>SCENE BOUNDARY ONLY (MergerFlip.Reset).  The registry and every screen handle die
        /// with the scene; nothing in any save is touched, here or anywhere else in this file.</summary>
        public static void Reset()
        {
            _partner.Clear(); _maxSeq.Clear(); _nonceOf.Clear(); _modelOwner.Clear();
            _openLast = null; _memberSig = "";
            _fDay = -1; _fType = ""; _fAmount = 0;                     // m4: the page's filter stash
            // m2: the row tint's per-label "original colour" map is keyed by objects that die with the
            // scene - it is emptied on the same boundary rather than held for objects that are gone.
            try { MPPatches.Patch_EconoViewLastTxCell_PartnerTint.ForgetLabelColours(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] label colour map clear: {ex.Message}"); }
            Plugin.Logger.LogInfo("[Feed] reset: tracking cleared (scene boundary).");
        }

        /// <summary>MAIN THREAD, 1 Hz from MergerFlip.Tick on every machine: clear on the way out of a
        /// company, and drop a departed owner's rows when the member set changes.</summary>
        public static void Tick()
        {
            try
            {
                if (!MergerSync.IAmMember)
                {
                    if (_memberSig.Length == 0) return;
                    _memberSig = "";
                    ClearAll("no longer in a company");
                    return;
                }
                string sig = MemberSignature();
                if (sig.Length > 0 && sig != _memberSig) { _memberSig = sig; DropNonMembers(); }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] membership tick: {ex.Message}"); }
        }

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
            catch { return ""; }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // R4 — ATTRIBUTION (the row tint's two halves)
        // ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>A list of ONE model type is about to be rebuilt: forget that type's rows only, so
        /// rebuilding one transactions screen cannot un-tint the other's.</summary>
        public static void ForgetModels(Type modelType)
        {
            if (_modelOwner.Count == 0) return;
            if (modelType == null) { _modelOwner.Clear(); return; }
            var drop = new List<object>();
            foreach (var kv in _modelOwner) if (kv.Key.GetType() == modelType) drop.Add(kv.Key);
            foreach (var k in drop) _modelOwner.Remove(k);
        }

        public static bool TryOwnerOfModel(object? model, out string pid)
        {
            pid = "";
            if (model == null || _modelOwner.Count == 0) return false;
            return _modelOwner.TryGetValue(model, out pid!) && !string.IsNullOrEmpty(pid);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // R6 — TESTDRIVE
        // ═══════════════════════════════════════════════════════════════════════════════════════

        private sealed class VerbRow
        {
            public long Stamp; public int Ord; public int Day; public string Type = ""; public float Amount; public string Pid = "own";
        }

        public static string TestDriveLine(string arg)
        {
            try
            {
                int rows = 0;
                if (!string.IsNullOrEmpty(arg) && !int.TryParse(arg.Trim(), out rows)) return "ERR feed: <n> must be a number";

                var all = new List<VerbRow>();
                int own = 0, ord = 0;
                var q = SaveGameManager.Current?.Transactions;
                if (q != null)
                    foreach (var t in q)
                    {
                        if (t == null) continue;
                        own++;
                        all.Add(new VerbRow { Stamp = Stamp(t), Ord = ord++, Day = t.timestamp != null ? t.timestamp.Day : -1, Type = t.transactionType ?? "", Amount = t.amount });
                    }
                foreach (var kv in _partner)
                    foreach (var e in kv.Value)
                        all.Add(new VerbRow { Stamp = Stamp(e), Ord = ord++, Day = e.Day, Type = e.Type ?? "", Amount = e.Amount, Pid = kv.Key });

                all.Sort((a, b) => { int c = b.Stamp.CompareTo(a.Stamp); return c != 0 ? c : b.Ord.CompareTo(a.Ord); });   // newest first, stable

                string last = all.Count == 0 ? "none" : $"{all[0].Day}:{all[0].Type}:{all[0].Amount:F2}";
                var sb = new System.Text.StringBuilder($"OK feed own={own} partner={PartnerCount} last={last}");
                for (int i = 0; i < rows && i < all.Count; i++)
                    sb.Append($" | {all[i].Day}:{all[i].Type}:{all[i].Amount:F2}:{all[i].Pid}");
                return sb.ToString();
            }
            catch (Exception ex) { return $"ERR feed: {ex.Message}"; }
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────

        private static long Stamp(Transaction t)
        {
            try { return t.timestamp == null ? 0L : (long)t.timestamp.Day * 10000L + t.timestamp.Hour * 100L + (long)t.timestamp.Minute; }
            catch { return 0L; }
        }

        private static long Stamp(PwTransaction e) => (long)e.Day * 10000L + e.Hour * 100L + e.Minute;

        private static bool TryAddressOf(string key, out Address addr)
        {
            addr = default!;
            if (string.IsNullOrEmpty(key)) return false;
            try
            {
                foreach (var reg in SaveGameManager.Current.BuildingRegistrations)
                    if (reg != null && GameStateReader.AddressKey(reg) == key) { addr = reg.Address; return true; }
            }
            catch { }
            return false;
        }
    }
}
