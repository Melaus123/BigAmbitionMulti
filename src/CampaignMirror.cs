using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// H-MERGERCAMPAIGN-1 (user-approved 2026-09-19, VIEW-ONLY).  Since the merger's recruitment leg a
    /// merged MEMBER can book a campaign for a partner's shop, but the campaign is only ever created in
    /// the OWNER's save (SharedShopWorkTabs.ApplyRoutedCampaignCreate) - so the member could not see the
    /// thing it had just paid for: RecruitmentAgencyDialog's 'manage campaigns' screen reads the LOCAL
    /// SaveGameManager.Current.RecruitmentCampaigns, and the entry that offers it is not even reachable
    /// without a local campaign at that agency.
    ///
    /// This file shows a merged co-member's running campaigns on that screen, READ-ONLY.
    ///
    /// WHY NOTHING IS INSTALLED.  A campaign's money and state live only in its real owner's save: the
    /// native hourly pass (Helpers/RecruitmentHelper.cs:28-39) walks
    /// SaveGameManager.Current.RecruitmentCampaigns and generates REAL candidates plus a finish message
    /// for every campaign it finds there.  A copy in a member's list would therefore hire people and
    /// congratulate the wrong player.  So the wire rows are held in an in-memory registry here and the
    /// row objects handed to the game's own row builder are TRANSIENT - built, drawn, and referenced by
    /// nothing afterwards.  The row builder takes its campaign BY ARGUMENT
    /// (UI.Dialog/RecruitmentCampaignsList.cs:29-50), which is exactly what makes that possible.
    ///
    /// TRANSPORT.  The rows ride the existing per-owner COMPANY LISTS bundle (PaperworkSync.Build ->
    /// host store -> CompanyLists.Extract -> MPServer.FanOutCompanyLists, and the joiner replay), so
    /// there is no new message type and no cadence of our own: dirty >= 30 s, urgent >= 2 s, the game-day
    /// change, and the membership rising edge.  Nothing is suppressed on "unchanged since last send" -
    /// the bundle is re-sent and re-applied in full, which is also the recurrence for a pass that had
    /// nothing to draw into yet.
    ///
    /// LIFECYCLE.  The registry never decides by itself that an owner is gone.  An owner's rows arrive with his
    /// company-lists bundle and leave with it: CompanyLists.ClearOwner (un-flip, unmerge, that owner left, his
    /// disconnect) calls ClearOwner here, and ClearAll covers leaving a company, a scene boundary and a
    /// disconnect of this machine.  A campaign is NOT carried by any absence hand-over list (a stand-in cannot
    /// tick one - see ApplyRoutedCampaignCreate's TrulyMine refusal), so an absent owner's campaigns are frozen
    /// in his own save and simply come back with his next bundle.
    /// </summary>
    public static class CampaignMirror
    {
        // ── state ────────────────────────────────────────────────────────────────────────────────

        /// <summary>ownerPid -> that owner's running campaigns as last published.  Session-only and
        /// screen-only: NOTHING here is ever added to any save list.</summary>
        private static readonly Dictionary<string, List<PwRecruitmentCampaign>> _byOwner =
            new Dictionary<string, List<PwRecruitmentCampaign>>(StringComparer.Ordinal);

        private static readonly HashSet<string> _loggedNoReg   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _loggedRefusal = new HashSet<string>(StringComparer.Ordinal);
        private static string _memberSig = "";

        /// <summary>DEV lever: how many owners' campaign rows this machine holds.</summary>
        public static int OwnerCount => _byOwner.Count;

        // ── reflection into the two private members the screen layer needs ───────────────────────

        private static readonly System.Reflection.FieldInfo? _fContractEntry =
            AccessTools.Field(typeof(UI.Dialog.RecruitmentCampaignsList), "contractEntry");
        private static readonly System.Reflection.MethodInfo? _mSetUpContract =
            AccessTools.Method(typeof(UI.Dialog.RecruitmentCampaignsList), "SetUpContract",
                               new[] { typeof(Entities.RecruitmentCampaign) });

        // ── shared helpers ───────────────────────────────────────────────────────────────────────

        private static bool Same(string? a, string? b)
            => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        /// <summary>An address key, or "" when the address is UNDEFINED (Streets/AddressHelper.cs:27-33
        /// lets an Address exist with an empty street, and a key built of two empty halves matches
        /// everything).  Same test TaskMirror.KeyOf makes.</summary>
        private static string KeyOf(Address? a)
        {
            if (a == null) return "";
            try { if (string.IsNullOrEmpty(a.streetName)) return ""; } catch { return ""; }
            try { return GameStateReader.AddressKey(a); } catch { return ""; }
        }

        // ── OWNER SIDE: fill the wire ────────────────────────────────────────────────────────────

        /// <summary>My OWN running campaigns as wire rows.  TRULY MINE only: a shop this machine merely
        /// STANDS IN for keeps its campaigns in its absent owner's save, never here, so there is nothing
        /// of theirs to publish from this machine.  Called from the paperwork bundle build, so it
        /// inherits that build's settled-world gate.</summary>
        public static List<PwRecruitmentCampaign> BuildRows()
        {
            var rows = new List<PwRecruitmentCampaign>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.RecruitmentCampaigns == null) return rows;
                foreach (var c in gi.RecruitmentCampaigns)
                {
                    if (c == null || c.finished) continue;          // a finished one leaves this same hour
                    string bizKey = KeyOf(c.businessAddress), agencyKey = KeyOf(c.agencyAddress);
                    if (bizKey.Length == 0 || agencyKey.Length == 0) continue;
                    BuildingRegistration? reg = null;
                    try { reg = Helpers.BuildingHelper.GetBuildingRegistration(c.businessAddress); } catch { reg = null; }
                    if (reg == null) continue;
                    bool mine = false;
                    try { mine = MergerFlip.TrulyMine(reg); } catch { mine = false; }
                    if (!mine) continue;

                    // The row's 'days left' is the LATEST find time (SetUpContract:36 orders by total
                    // minutes and takes .Last()), so that ONE timestamp is all the wire has to carry.
                    int day = -1, hour = 0; float best = float.MinValue;
                    try
                    {
                        foreach (var t in c.candidateFindTimes ?? new List<BigAmbitions.DayNightCycle.Timestamp>())
                        {
                            if (t == null) continue;
                            float m; try { m = t.GetTotalMinutes(); } catch { continue; }
                            if (m <= best) continue;
                            best = m; day = t.Day; hour = t.Hour;
                        }
                    }
                    catch { }
                    if (day < 0) { day = gi.Day; hour = 0; }        // an empty list would THROW natively - never ship one

                    rows.Add(new PwRecruitmentCampaign
                    {
                        OwnerPid           = MPConfig.PlayerId,
                        AgencyKey          = agencyKey,
                        BusinessAddressKey = bizKey,
                        SkillName          = c.skillRequirement?.skillName ?? "",
                        FullTime           = c.fullTime,
                        PartTime           = c.partTime,
                        AmountOfCandidates = c.amountOfCandidates,
                        CandidatesFound    = c.candidatesFound,
                        LastFindDay        = day,
                        LastFindHour       = hour,
                        Price              = c.price,
                    });
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] owner rows: {ex.Message}"); }
            return rows;
        }

        // ── OWNER SIDE: the dirty marks ──────────────────────────────────────────────────────────
        //
        // Three seams, all of them "the list I publish actually changed": a booking confirmed in the
        // native dialog, a booking that arrived as a routed leg, a cancellation - and the hourly pass,
        // which is compared before/after so an hour that changed nothing costs no bundle.

        /// <summary>The publishable shape of my own list, cheap enough to take twice around one call.
        /// "" whenever there is nothing to publish (not a member / no save), which makes the two
        /// signatures equal and the seam silent.</summary>
        private static string Signature()
        {
            try
            {
                if (!MergerSync.IAmMember) return "";
                var sb = new System.Text.StringBuilder();
                foreach (var r in BuildRows())
                    sb.Append(r.BusinessAddressKey).Append('|').Append(r.AgencyKey).Append('|')
                      .Append(r.SkillName).Append('|').Append(r.AmountOfCandidates).Append('/')
                      .Append(r.CandidatesFound).Append('|').Append(r.LastFindDay).Append(';');
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>One of MY campaigns was created, cancelled or ticked on.  URGENT: a co-member is
        /// looking at the agency screen this feeds, so the bundle goes at two seconds, not thirty.</summary>
        public static void MarkOwnChange(string why, bool urgent = true)
        {
            try
            {
                if (!MergerSync.IAmMember) return;
                if (urgent) PaperworkSync.MarkUrgent(); else PaperworkSync.MarkDirty();
                Plugin.Logger.LogInfo($"[Campaigns] my campaign list changed ({why}) - the company-lists bundle is {(urgent ? "urgent" : "dirty")}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] dirty mark ({why}): {ex.Message}"); }
        }

        /// <summary>RecruitmentAgencyDialog.cs:163-200 - the native confirm.  A SIGNATURE diff and not a
        /// blind mark, because the merger gate's prefix can skip this method entirely (the booking was
        /// routed to the owner and nothing was added here), and an invalid-input return adds nothing
        /// either.</summary>
        [HarmonyPatch(typeof(Dialogs.RecruitmentAgencyDialog), "OnRecruitmentSettingsSet")]
        public static class Patch_RecruitmentAgencyDialog_OnRecruitmentSettingsSet_OwnerDirty
        {
            static void Prefix(out string __state)
            { __state = ""; try { __state = Signature(); } catch { } }

            static void Postfix(string __state)
            {
                try { if (Signature() != __state) MarkOwnChange("a campaign was booked here"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] confirm seam: {ex.Message}"); }
            }
        }

        /// <summary>RecruitmentAgencyDialog.cs:234-245 - the cancel button's own handler.</summary>
        [HarmonyPatch(typeof(Dialogs.RecruitmentAgencyDialog), nameof(Dialogs.RecruitmentAgencyDialog.OnCancelCampaign))]
        public static class Patch_RecruitmentAgencyDialog_OnCancelCampaign_OwnerDirty
        {
            static void Prefix(out string __state)
            { __state = ""; try { __state = Signature(); } catch { } }

            static void Postfix(string __state)
            {
                try { if (Signature() != __state) MarkOwnChange("a campaign was cancelled here"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] cancel seam: {ex.Message}"); }
            }
        }

        /// <summary>Helpers/RecruitmentHelper.cs:28-39 - the hourly pass raises candidatesFound and
        /// REMOVES a finished campaign.  Once a GAME HOUR, never per frame, and the two signatures are
        /// compared so an hour in which nothing was found costs nothing at all.</summary>
        [HarmonyPatch(typeof(Helpers.RecruitmentHelper), nameof(Helpers.RecruitmentHelper.RunHourly))]
        public static class Patch_RecruitmentHelper_RunHourly_OwnerDirty
        {
            static void Prefix(out string __state)
            { __state = ""; try { __state = Signature(); } catch { } }

            static void Postfix(string __state)
            {
                try { if (Signature() != __state) MarkOwnChange("the hourly pass changed it", urgent: false); }   // a background event: the 30 s dirty cadence (review LOW-5)
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] hourly seam: {ex.Message}"); }
            }
        }

        // ── MEMBER SIDE: the registry ────────────────────────────────────────────────────────────

        /// <summary>MAIN THREAD, from CompanyLists.Apply.  One owner's rows replace that owner's whole
        /// set - the bundle is re-applied in full every time, so there is no per-row reconcile.</summary>
        public static void Install(CompanyListsPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.OwnerPid)) return;
                var rows = new List<PwRecruitmentCampaign>();
                foreach (var r in p.RecruitmentCampaigns ?? new List<PwRecruitmentCampaign>())
                {
                    if (r == null || string.IsNullOrEmpty(r.AgencyKey)) continue;
                    if (string.IsNullOrEmpty(r.OwnerPid)) r.OwnerPid = p.OwnerPid;
                    rows.Add(r);
                }
                bool had = _byOwner.TryGetValue(p.OwnerPid, out var before) && before != null && before.Count > 0;
                if (rows.Count == 0 && !had) { _byOwner.Remove(p.OwnerPid); return; }
                _byOwner[p.OwnerPid] = rows;
                if (rows.Count > 0 || had)
                    Plugin.Logger.LogInfo($"[Campaigns] mirrored {rows.Count} running campaign(s) of '{p.OwnerPid}' (view only - nothing installed).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] install '{p?.OwnerPid}': {ex.Message}"); }
        }

        /// <summary>One owner's rows go: they left the company, their buildings un-flipped, the session
        /// dropped.  NOT called when that owner merely goes ABSENT - see the class comment.</summary>
        public static void ClearOwner(string ownerPid, string why)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerPid)) return;
                if (_byOwner.Remove(ownerPid))
                    Plugin.Logger.LogInfo($"[Campaigns] cleared the mirrored campaigns of '{ownerPid}' - {why}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] clear '{ownerPid}': {ex.Message}"); }
        }

        public static void ClearAll(string why)
        {
            try
            {
                if (_byOwner.Count == 0) { _memberSig = ""; return; }
                int n = _byOwner.Count;
                _byOwner.Clear(); _memberSig = "";
                Plugin.Logger.LogInfo($"[Campaigns] cleared the mirrored campaigns of {n} owner(s) - {why}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] clear all ({why}): {ex.Message}"); }
        }

        /// <summary>MAIN THREAD, 1 Hz from MergerFlip.Tick, exactly like CompanyFeed's: drop everything on
        /// the way out of a company.  A single owner's departure is NOT inferred here (re-check MEDIUM): it
        /// arrives explicitly through CompanyLists.ClearOwner -> ClearOwner.</summary>
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
                if (sig.Length == 0 || sig == _memberSig) return;
                _memberSig = sig;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] membership tick: {ex.Message}"); }
        }

        private static string MemberSignature()
        {
            try
            {
                var names = new List<string>();
                foreach (var m in MergerSync.MemberNames) names.Add(m ?? "");
                names.Sort(StringComparer.Ordinal);
                return string.Join(",", names.ToArray());
            }
            catch { return ""; }
        }

        // ── MEMBER SIDE: DTO -> a TRANSIENT campaign the game's own row builder can draw ──────────

        /// <summary>The wire row as a live RecruitmentCampaign that is in NO list anywhere.  Null when
        /// this machine cannot resolve the two addresses or the business registration, because the native
        /// row builder dereferences GetBuildingRegistration(businessAddress).BusinessName with no null
        /// check (RecruitmentCampaignsList.cs:32).  candidateFindTimes gets exactly ONE timestamp so the
        /// builder's `.Last()` (:36) is always safe.</summary>
        internal static Entities.RecruitmentCampaign? ToTransient(PwRecruitmentCampaign r)
        {
            try
            {
                if (r == null) return null;
                var agency = MergerAbsence.AddressOfKey(r.AgencyKey ?? "");
                var biz    = MergerAbsence.AddressOfKey(r.BusinessAddressKey ?? "");
                if (agency == null || biz == null)
                {
                    if (_loggedNoReg.Add(r.BusinessAddressKey ?? ""))
                        Plugin.Logger.LogInfo($"[Campaigns] '{r.BusinessAddressKey}' / '{r.AgencyKey}' does not resolve to an address here "
                                            + "- that campaign is not drawn (once per business).");
                    return null;
                }
                BuildingRegistration? reg = null;
                try { reg = Helpers.BuildingHelper.GetBuildingRegistration(biz); } catch { reg = null; }
                if (reg == null)
                {
                    if (_loggedNoReg.Add(r.BusinessAddressKey ?? ""))
                        Plugin.Logger.LogInfo($"[Campaigns] '{r.BusinessAddressKey}' has no business registration here "
                                            + "- that campaign is not drawn, because the game's own row builder dereferences it (once per business).");
                    return null;
                }
                return new Entities.RecruitmentCampaign
                {
                    agencyAddress      = agency,
                    businessAddress    = biz,
                    skillRequirement   = new Entities.RecruitmentCampaign.SkillRequirement
                                         { skillName = r.SkillName ?? "", percentage = 20f },
                    fullTime           = r.FullTime,
                    partTime           = r.PartTime,
                    amountOfCandidates = r.AmountOfCandidates,
                    candidatesFound    = r.CandidatesFound,
                    price              = r.Price,
                    candidateFindTimes = new List<BigAmbitions.DayNightCycle.Timestamp>
                                         { new BigAmbitions.DayNightCycle.Timestamp(r.LastFindDay, r.LastFindHour, 0f) },
                };
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] transient row: {ex.Message}"); return null; }
        }

        /// <summary>Every merged co-member's rows for ONE agency, in owner order.</summary>
        private static List<PwRecruitmentCampaign> RowsForAgency(string agencyKey)
        {
            var rows = new List<PwRecruitmentCampaign>();
            if (string.IsNullOrEmpty(agencyKey) || _byOwner.Count == 0) return rows;
            foreach (var kv in _byOwner)
            {
                // No membership test here: the registry IS the truth - an owner's rows are removed only by the
                // explicit company-lists retirement (see LIFECYCLE in the header).
                foreach (var r in kv.Value ?? new List<PwRecruitmentCampaign>())
                    if (r != null && Same(r.AgencyKey, agencyKey)) rows.Add(r);
            }
            return rows;
        }

        /// <summary>Does a merged co-member have a campaign running at this agency?  The ctor gate's
        /// question.</summary>
        internal static bool HasAnyForAgency(string agencyKey) => RowsForAgency(agencyKey).Count > 0;

        // ── MEMBER SIDE: the screen layer ────────────────────────────────────────────────────────

        /// <summary>UI.Dialog/RecruitmentCampaignsList.cs:18-27 has filtered the LOCAL list and drawn its
        /// rows; the partner's rows go in after them.  Each one is handed to the list's OWN private
        /// SetUpContract, so it is the game's own row with the game's own words - we add no text.  The
        /// row it just built is found the way CompanyPlans.DrawUnionRow finds one (childCount before and
        /// after), and its cancel button is disarmed there and then: the listener SetUpContract attached
        /// would call OnCancelCampaign with an object that is in no save list, which on this machine
        /// removes nothing and sends the wrong player two phone messages.</summary>
        [HarmonyPatch(typeof(UI.Dialog.RecruitmentCampaignsList), "Start")]
        public static class Patch_RecruitmentCampaignsList_Start_MirrorRows
        {
            static void Postfix(UI.Dialog.RecruitmentCampaignsList __instance)
            {
                try { DrawMirroredRows(__instance); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] mirror rows: {ex.Message}"); }
            }
        }

        /// <summary>Returns how many partner rows were drawn.</summary>
        private static int DrawMirroredRows(UI.Dialog.RecruitmentCampaignsList list)
        {
            if (list == null || _byOwner.Count == 0) return 0;
            if (_fContractEntry == null || _mSetUpContract == null)
            {
                if (_loggedRefusal.Add("members"))
                    Plugin.Logger.LogWarning("[Campaigns] RecruitmentCampaignsList.contractEntry / SetUpContract are not where they were "
                                           + "- a partner's campaigns cannot be drawn on this screen (once).");
                return 0;
            }
            var ctrl = DialogController.current;
            if (ctrl == null || ctrl.contact == null) return 0;
            string agencyKey = KeyOf(ctrl.contact.Address);
            if (agencyKey.Length == 0) return 0;
            var template = _fContractEntry.GetValue(list) as Transform;
            if (template == null) return 0;
            var parent = template.parent;
            // Review MEDIUM-3: without a parent the new row could not be found afterwards, and its native cancel
            // listener would stay ARMED over a transient campaign (a false 'campaign cancelled' conversation).
            if (parent == null) return 0;

            int drawn = 0;
            foreach (var r in RowsForAgency(agencyKey))
            {
                var c = ToTransient(r);
                if (c == null) continue;
                int before = parent != null ? parent.childCount : 0;
                try { _mSetUpContract.Invoke(list, new object[] { c }); }
                catch (Exception ex)
                {
                    if (_loggedRefusal.Add("draw"))
                        Plugin.Logger.LogWarning($"[Campaigns] a partner's campaign row could not be drawn: {ex.Message} (once).");
                    continue;
                }
                if (parent != null)
                    for (int i = before; i < parent.childCount; i++) Disarm(parent.GetChild(i));
                drawn++;
            }
            if (drawn > 0)
                Plugin.Logger.LogInfo($"[Campaigns] drew {drawn} partner campaign row(s) at '{agencyKey}' - read-only, cancel disabled.");
            return drawn;
        }

        /// <summary>Take the cancel control out of a mirrored row: the runtime listener comes off AND the
        /// button is greyed, so neither a click nor a scripted invoke can reach OnCancelCampaign with a
        /// campaign this save does not hold.  Greying a control is the one presentation change this
        /// feature makes - no text of ours goes on the screen.</summary>
        private static void Disarm(Transform row)
        {
            try
            {
                if (row == null) return;
                var t = row.Find("Buttons/CancelCampaignButton");
                var btn = t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
                if (btn == null)
                {
                    if (_loggedRefusal.Add("cancelbtn"))
                        Plugin.Logger.LogWarning("[Campaigns] a mirrored row has no 'Buttons/CancelCampaignButton' - it is left undrawn-safe "
                                               + "but could not be greyed (once).");
                    return;
                }
                try { btn.onClick.RemoveAllListeners(); } catch { }
                btn.interactable = false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] disarm cancel: {ex.Message}"); }
        }

        // ── MEMBER SIDE: the dialog gate ─────────────────────────────────────────────────────────

        /// <summary>THE AGENCY'S FRONT DOOR (batch 18 review HIGH-1).  The native constructor
        /// (Dialogs/RecruitmentAgencyDialog.cs:18-42) offers 'start new / manage campaigns' ONLY when the LOCAL
        /// list holds a campaign at this agency (:21-25); a member whose only campaign here lives in a merged
        /// partner's save got the plain start entry and no way in to the list.  Showing our own entry AFTER the
        /// constructor does not work: DialogController.ShowEntry fades an entry in for a second and then runs its
        /// OnVisible, so the start entry's settings form landed on top of ours a second later (and the greeting
        /// was sent twice).  Instead the NATIVE constructor is made to take its own :21-25 branch: for the
        /// SYNCHRONOUS duration of the constructor call a bare sentinel campaign naming this agency sits in the
        /// local list - added by the patch's Prefix, removed by its FINALIZER, always.  Nothing can tick, save or
        /// sync inside a constructor call (single-threaded), the same shape as the tenancy raise that patch
        /// already performs.  NEVER over the diploma-missing or no-business branches: the same two tests run
        /// here first (the access gate is already open, so the business test reads the list the constructor
        /// will read).  Returns true when a sentinel was placed.</summary>
        internal static bool BeginCtorSentinel()
        {
            try
            {
                if (_ctorSentinel != null) return false;   // single slot: a nested constructor must never orphan the outer sentinel
                if (_byOwner.Count == 0 || !MergerSync.IAmMember) return false;
                var gi = SaveGameManager.Current;
                var ctrl = DialogController.current;
                if (gi == null || ctrl == null || ctrl.contact == null || gi.RecruitmentCampaigns == null) return false;
                string agencyKey = KeyOf(ctrl.contact.Address);
                if (agencyKey.Length == 0) return false;

                // :21-25 - a local campaign here: the native constructor takes that branch on its own.
                foreach (var c in gi.RecruitmentCampaigns)
                    if (c != null && Same(KeyOf(c.agencyAddress), agencyKey)) return false;

                // :27-31 - without the BasicHr diploma this agency does not talk business, mirror or no mirror.
                bool diploma = false;
                var diplomas = gi.PlayerDiplomas;
                if (diplomas != null)
                    foreach (var d in diplomas)
                        if (d != null && d.name == DiplomaName.BasicHr && d.completed) { diploma = true; break; }
                if (!diploma) return false;

                // :26 + :32-36 - nor over the no-business branch.
                bool anyBusiness = false;
                var regs = gi.BuildingRegistrations;
                if (regs != null)
                    foreach (var reg in regs)
                        if (reg != null && !string.IsNullOrWhiteSpace(reg.BusinessName) && reg.RentedByPlayer) { anyBusiness = true; break; }
                if (!anyBusiness) return false;

                if (!HasAnyForAgency(agencyKey)) return false;

                // The SAME Address object the constructor compares against, so `x.agencyAddress == contact.Address`
                // holds whatever Address equality is.
                var sentinel = new Entities.RecruitmentCampaign { agencyAddress = ctrl.contact.Address };
                gi.RecruitmentCampaigns.Add(sentinel);
                _ctorSentinel = sentinel;
                if (_loggedRefusal.Add("offered|" + agencyKey))
                    Plugin.Logger.LogInfo($"[Campaigns] '{agencyKey}': no campaign of mine here, but a merged partner has one - "
                                        + "the agency opens on its own 'start new / manage campaigns' entry (once per agency).");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] agency door (begin): {ex.Message}"); return false; }
        }

        /// <summary>FINALIZER half: the sentinel leaves the local list, whatever the constructor did.</summary>
        internal static void EndCtorSentinel()
        {
            var sentinel = _ctorSentinel; _ctorSentinel = null;
            if (sentinel == null) return;
            try
            {
                var list = SaveGameManager.Current?.RecruitmentCampaigns;
                bool removed = list != null && list.Remove(sentinel);
                if (!removed)
                    Plugin.Logger.LogWarning("[Campaigns] agency door (end): the constructor sentinel was NOT in the local campaign list any more.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Campaigns] agency door (end): {ex.Message}"); }
        }

        private static Entities.RecruitmentCampaign? _ctorSentinel;   // lives only between the ctor patch's Prefix and Finalizer

        // ── DEV LEVERS ───────────────────────────────────────────────────────────────────────────

        /// <summary>`campaigns mirror [&lt;agency key&gt;]`.  Read-only.  With an agency key it also
        /// MATERIALISES that agency's rows through the real DTO -&gt; transient path (everything the
        /// screen layer does short of the draw itself, which needs a dialog nobody opens in the rig), so
        /// a conversion that would fail in front of a player fails here instead.</summary>
        public static string TestDriveLine(string arg)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("OK campaigns mirror owners=").Append(_byOwner.Count).Append(" [");
                int n = 0;
                foreach (var kv in _byOwner)
                    foreach (var r in kv.Value ?? new List<PwRecruitmentCampaign>())
                    {
                        if (r == null) continue;
                        if (n++ > 0) sb.Append(';');
                        sb.Append(kv.Key).Append(':').Append(r.BusinessAddressKey).Append('|')
                          .Append(r.SkillName).Append('|').Append(r.CandidatesFound).Append('/')
                          .Append(r.AmountOfCandidates).Append('|').Append(r.AgencyKey);
                    }
                sb.Append(']');
                int rows = 0;
                if (arg.Length > 0)
                    foreach (var r in RowsForAgency(arg))
                        if (ToTransient(r) != null) rows++;
                sb.Append(" rows=").Append(rows);
                return sb.ToString();
            }
            catch (Exception ex) { return "ERR campaigns mirror: " + ex.Message; }
        }

        /// <summary>`campaigns agencies`.  Read-only, and the ONLY runtime way a rig can name a
        /// recruitment agency: the game itself finds one by businessTypeName
        /// (Helpers/RecruitmentCommands.cs:109-116), and this reads the same field.</summary>
        public static string AgenciesLine()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return "ERR no save loaded";
                var keys = new List<string>();
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    string type = ""; try { type = reg.businessTypeName ?? ""; } catch { }
                    if (!string.Equals(type, "ba:businesstype_recruitmentagency", StringComparison.OrdinalIgnoreCase)) continue;
                    string k = ""; try { k = GameStateReader.AddressKey(reg); } catch { }
                    if (k.Length > 0 && !keys.Contains(k)) keys.Add(k);
                }
                keys.Sort(StringComparer.Ordinal);
                return $"OK campaigns agencies={keys.Count} [{string.Join(";", keys.ToArray())}]";
            }
            catch (Exception ex) { return "ERR campaigns agencies: " + ex.Message; }
        }
    }
}
