using System;
using System.Collections.Generic;
using Entities;                                 // Contact, TextMessage, AdditionalMessageData
using HarmonyLib;
using Helpers;                                  // EmployeeHelper
using UI.Smartphone.Apps.Contacts;              // ContactCategoryName, ContextButton
using UI.Smartphone.Apps.MyEmployees;           // MyEmployees - the claim seam's one entry
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// MERGER PHASE 4b (PEOPLE) - P4, THE PHONE RELAY (D20-5).
    ///
    /// A merged company is ONE business, but the phone is per save: a message is raised on the machine
    /// that holds the shop or the person (decompile Entities/Contact.cs:98-119), so a co-member never
    /// learns that a partner's shop was messaged at all. This file is the relay, on the three legs the
    /// notification relay already proved (NotificationRelay.cs:137-180 sender gate, the host fan-out,
    /// :199-222 receiver re-raise):
    ///
    ///  - SEND. A postfix on the game's own gateway (Contact.SendMessage - every message funnels through
    ///    it) relays a message whose CONTACT is a business of mine (gated on MergerFlip.TrulyMine, the
    ///    same true-ownership test, so an event travels once) or one of my own people. Nothing else
    ///    travels: personal, finance, tutorial and rival messages stay where they are raised.
    ///  - RECEIVE. The receiver re-raises it through the game's OWN Contact.GetContact +
    ///    Contact.SendMessage, so the contact auto-creates (decompile Entities/Contact.cs:206-214), the
    ///    badge moves and the "new message" toast carries the game's own wording. NO NEW TEXT.
    ///  - PRESS. A relayed copy's buttons carry the ORIGIN's descriptors (label key + the game's own
    ///    background colour) with OUR click, which asks the host to have the OWNER run the original
    ///    closure - once. The owner then relays "handled by &lt;pid&gt;" and every other copy clears its
    ///    buttons and is marked read, which is exactly what the game does to a message it pressed itself
    ///    (ContextButton.SetUp: onClick, then contextButtonData.Clear() + SetContextButtonsNoninteractable).
    ///
    /// THE TWO CONTEXT ACTIONS ARE NOT PRESSED REMOTELY (decompile TextMessage.cs:96-114):
    ///  * SalaryNegotiation acts on the presser's own candidateSalaryNegotiations, so a relayed copy
    ///    carries ONE button with the game's own "dialog_negotiate_button" key that opens the negotiation
    ///    LOCALLY on the shared-pool candidate - through build A's claim seam
    ///    (MyEmployees.NegotiateWithCandidate -> CompanyCandidates.DeferNegotiationToHost), so the claim
    ///    names the CANDIDATE id and the accept stays host-gated. There is no second claim mechanism.
    ///  * HealthInsurancePlanOffer is the HR plan's employee insurance (decompile
    ///    Entities/HealthInsurancePlanOffer.cs:88 writes HrManagerPlan.healthInsurancePlan). Plan and offer
    ///    live only in the owner's save - a member holds HR plans as 4c part 1's DETACHED screen-layer rows,
    ///    never as game objects - so a relayed copy is INFORMATIONAL and its buttons stay on the owner's
    ///    machine. Reported as a decision.
    ///
    /// A RELAYED COPY IS NEVER A NATIVE RECORD: its contextAction is stripped, which is what stops the
    /// message delete (decompile ContactsApp.cs:452-465) and Contact.CleanOldMessages (:161-177) from
    /// discarding the owner's candidate or offer through a copy; and every relayed message is lifted out of
    /// both save choke points. A relayed COMPLAINT never reaches StartComplaint here, so it cannot enqueue
    /// into this save's daysWithComplaints - the receiver's 2-per-week cap (ComplaintHelper.cs:32-39) is
    /// untouched by the company's complaints.
    ///
    /// INERT outside a merger: the postfix returns at MergerSync.IAmMember and nothing is ever injected.
    /// </summary>
    public static class CompanyMessages
    {
        private const string Tag = "[Messages]";
        private const int MaxTracked = 200;

        // -- state --

        /// <summary>MINE: a message this machine raised and relayed - the ORIGINAL TextMessage, whose
        /// additionalData still holds the live onClick closures a press has to run.</summary>
        private static readonly Dictionary<string, (Contact contact, TextMessage msg, string key, string person)> _mine = new();

        /// <summary>A COPY of a partner's message: the origin, the contact it hangs off, and the copy.</summary>
        private static readonly Dictionary<string, (string owner, Contact contact, TextMessage msg)> _copies = new();

        private static readonly HashSet<string> _handled = new();          // message ids already run on the owner

        /// <summary>r2 MAJOR-2: a press THIS machine sent and is still waiting on, with the button
        /// descriptors the game wiped off the copy the instant the click returned (decompile
        /// ContextButton.cs:77). They go back on the copy if the press is refused, and are dropped when
        /// 'handled' or 'refused' resolves it - so a refused press is never silently lost.</summary>
        private static readonly Dictionary<string, PendingPress> _pending = new();

        /// <summary>r2 MAJOR-3: a press in flight is not just its descriptors any more - the button index and
        /// the clock are kept too, so the tick can ASK AGAIN. Without a re-ask, an owner that dropped between
        /// the host's hand-off and its own run left this entry standing for ever and every later click was
        /// answered "a press is already in flight".</summary>
        private sealed class PendingPress
        {
            public List<TextMessage.ContextButtonData> Kept;
            public int   ButtonIndex;
            public float LastSent;
        }

        /// <summary>Real seconds between re-asks of an unanswered press (r2 MAJOR-3). A repeat is harmless:
        /// the owner answers 'handled' for an id it has already run.</summary>
        private const float PressRetrySeconds = 30f;
        private static readonly HashSet<Contact> _createdHere = new();     // contacts a relay auto-created here

        /// <summary>HO-1c L1: a relay-created contact -> the member whose message made it appear here.  Keyed by
        /// the Contact OBJECT (contacts are keyed by NAME, and a colour must never touch that key).</summary>
        private static readonly Dictionary<Contact, string> _relayOwner = new();
        private static readonly Dictionary<string, string> _empOwnerByName = new();   // employee-contact id (a NAME) -> owner pid
        private static float _empOwnerAt;

        /// <summary>HO-1c L1: whose member's message created this contact here ("" = nobody's).  Also answers for
        /// an EMPLOYEES contact whose id NAMES an injected partner employee: MPRegisterSync offers no name lookup
        /// (only IsInjectedStaff/OwnerOfInjected, both by id), so the roster is scanned by character name - the id
        /// a native employee contact carries (decompile ContactsApp.cs:739) - and the answer is cached for 5 s
        /// because the contacts list rebinds its recycled rows on every scroll frame.</summary>
        public static string OwnerOfRelayContact(Contact c)
        {
            try
            {
                if (c == null) return "";
                if (_relayOwner.TryGetValue(c, out var pid) && !string.IsNullOrEmpty(pid)) return pid;
                string cid = c.id ?? "";
                if (cid.Length == 0 || c.category != ContactCategoryName.Employees) return "";
                float now = UnityEngine.Time.unscaledTime;
                if (now - _empOwnerAt > 5f) { _empOwnerByName.Clear(); _empOwnerAt = now; }
                if (_empOwnerByName.TryGetValue(cid, out var cached)) return cached;
                string found = "";
                try
                {
                    var roster = Helpers.EmployeeHelper.GetEmployeeInstances();
                    if (roster != null)
                        foreach (var e in roster)
                        {
                            if (e == null || e.characterData == null || e.characterData.name != cid) continue;
                            if (MPRegisterSync.IsInjectedStaff(e.id)) found = MPRegisterSync.OwnerOfInjected(e.id);
                            break;
                        }
                }
                catch { }
                _empOwnerByName[cid] = found;
                return found;
            }
            catch { return ""; }
        }
        private static readonly List<string> _order = new();               // insertion order: the prune and the readout
        private static readonly HashSet<string> _logged = new();
        private static int _seq;

        /// <summary>Set while THIS machine is re-raising a relayed message, so the send postfix never
        /// relays a relay. Thread-static for the reason the notification relay's is.</summary>
        [ThreadStatic] private static bool _applying;

        public static int CopyCount => _copies.Count;

        // -- bounds --

        /// <summary>A relayed message is network input: bounded at the host before the fan-out and again at
        /// the receiver before anything is written into this save's contacts.</summary>
        internal static bool PayloadSane(CompanyMessagePayload p, string where)
        {
            string why = null;
            if (p == null) why = "null payload";
            else if (string.IsNullOrEmpty(p.Action) || p.Action.Length > 16) why = "action length";
            else if (string.IsNullOrEmpty(p.MessageId) || p.MessageId.Length > 64) why = "message id length";
            else if (p.Action == "msg")
            {
                if (string.IsNullOrEmpty(p.MessageKey) || p.MessageKey.Length > 160) why = "message key length";
                else if (string.IsNullOrEmpty(p.ContactId) || p.ContactId.Length > 96) why = "contact id length";
                else if ((p.ContactDescription?.Length ?? 0) > 96) why = "contact description length";
                else if (!Enum.IsDefined(typeof(ContactCategoryName), p.ContactCategory)) why = $"category {p.ContactCategory} undefined";
                else if ((p.Buttons?.Count ?? 0) > 6) why = $"{p.Buttons.Count} buttons";
                else if (p.Data != null)
                {
                    if (p.Data.Count > 24) why = $"{p.Data.Count} data entries";
                    else foreach (var kv in p.Data)
                        if ((kv.Key?.Length ?? 0) > 64 || (kv.Value?.Length ?? 0) > 512) { why = "data entry length"; break; }
                }
                if (why == null && p.Buttons != null)
                    foreach (var b in p.Buttons)
                        if (b == null || string.IsNullOrEmpty(b.Key) || b.Key.Length > 96) { why = "button key length"; break; }
            }
            else if (p.Action == "refused")
            {
                if ((p.Reason?.Length ?? 0) > 32) why = "reason length";
                else if ((p.TargetPid?.Length ?? 0) > 64) why = "target id length";
            }
            if (why == null) return true;
            Plugin.Logger.LogWarning($"{Tag} {where}: dropped '{p?.Action}' from '{p?.PlayerId}' - {why}.");
            return false;
        }

        // -- tick --

        /// <summary>MAIN THREAD (SharedShopStaff.Tick). Nothing publishes on a timer here - a message is an
        /// event - so this only makes the "inert without a company" promise good.</summary>
        public static void Tick()
        {
            try
            {
                if (!MergerSync.IAmMember)
                {
                    if (_copies.Count > 0 || _mine.Count > 0) ClearAll("this player is not in a company");
                    return;
                }
                PruneVanishedCopies();      // r2 MINOR-6
                RetryPendingPresses();      // r2 MAJOR-3
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} tick: {ex.GetType().Name}: {ex.Message}"); }
        }

        // -- C1 SENDER --

        /// <summary>THE GATEWAY postfix. The message has already been raised natively here; the relay never
        /// changes what this machine shows.</summary>
        public static void OnLocalMessage(Contact contact, TextMessage msg, bool notify)
        {
            try { RelayNow(contact, msg, notify); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} send: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>The relay itself. Returns the id it travelled under, or null when the gate stopped it -
        /// the TestDrive lever re-sends through this same method, so it can never take a different path.</summary>
        private static string RelayNow(Contact contact, TextMessage msg, bool notify)
        {
            if (_applying) return null;                                   // never relay a relay
            if (contact == null || msg == null) return null;
            if (msg.isFromPlayer) return null;                            // the player's own outgoing line
            if (!MergerSync.IAmMember) return null;
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return null;
            if (IsRelayedCopy(msg)) return null;                          // a copy that arrived here

            string addressKey = OwnedContactAddress(contact, out string why, out string personId);
            if (why != null)
            {
                // BUILD POPUPS-1 P3/P4: two families of company news land on a contact NOBODY owns, so the
                // ownership gate can never pass them.  They are recognised by their message KEY instead.
                string byKey = ForeignContactRelayAddress(contact, msg, out string keyWhy);
                if (byKey == null)
                {
                    if (_logged.Add("skip|" + contact.id))
                        Plugin.Logger.LogInfo($"{Tag} '{contact.id}' is not a company contact of mine ({why}{(keyWhy != null ? "; " + keyWhy : "")}) - its messages stay on this machine.");
                    return null;
                }
                addressKey = byKey; personId = "";
            }

            var (day, hourOfDay) = GameStateReader.GetGameTime();
            long stamp = (long)day * 1440L + (long)(hourOfDay * 60f);
            // r2 CAVEAT: a RE-SEND of a message this machine already tracks keeps its ORIGINAL id. Minting a
            // second id for the same TextMessage would track it twice, put two copies on every receiver, and
            // make a press on the second copy unanswerable ('nobutton' - the first copy answered it).
            string id = FindMineId(msg);
            bool firstTime = id == null;
            if (firstTime) id = "bamp-msg-" + Fnv($"{MPConfig.PlayerId}|{contact.id}|{msg.messageKey}|{stamp}|{++_seq}");

            var p = new CompanyMessagePayload
            {
                PlayerId           = MPConfig.PlayerId,
                Action             = "msg",
                MessageId          = id,
                OwnerPid           = MPConfig.PlayerId,
                AddressKey         = addressKey,
                ContactId          = contact.id ?? "",
                ContactCategory    = (int)contact.category,
                ContactDescription = contact.description ?? "",
                StreetName         = contact.streetName ?? "",
                StreetNumber       = contact.streetNumber,
                MessageKey         = msg.messageKey ?? "",
                Data               = msg.messageData == null ? new Dictionary<string, string>() : new Dictionary<string, string>(msg.messageData),
                IsSpecial          = msg.isSpecialMessage,
                IsNewInteraction   = msg.isNewInteraction,
                Notify             = notify,
                StampMinute        = stamp,
            };

            // The button DESCRIPTORS travel (label key + the game's own background colour); the closures
            // cannot - they are live UnityActions over this save's objects, which is exactly why a press
            // comes back here to be run.
            var buttons = msg.additionalData?.contextButtonData;
            if (buttons != null)
                foreach (var b in buttons)
                    p.Buttons.Add(new RelayButton { Key = b.key ?? "", Colour = (int)b.backgroundColor });

            if (msg.contextAction != null)
            {
                p.CtxType       = (int)msg.contextAction.type;
                p.CtxEmployeeId = msg.contextAction.employeeInstanceId ?? "";
                p.CtxOfferId    = msg.contextAction.healthPlanOfferId ?? "";
                // D23: a relayed staff-insurance offer must be ACTIONABLE on every member, so the offer's
                // own terms travel with it. The offer object itself stays here - these five rebuild a
                // detached copy on the member that is good enough for the game's own negotiation dialog,
                // and the accept/decline route back to whoever runs the HR plan's headquarters.
                if (p.CtxType == (int)TextMessage.ContextAction.ContextActionType.HealthInsurancePlanOffer
                    && p.CtxOfferId.Length > 0)
                    FillInsuranceTerms(p);
            }

            if (!PayloadSane(p, "sender")) return null;
            if (firstTime) Track(id, contact, msg, null, personId);
            Send(p);
            Plugin.Logger.LogInfo($"{Tag} relaying '{p.MessageKey}' on '{p.ContactId}' as {id} ({p.Buttons.Count} button(s), context={p.CtxType}) to my company.");
            return id;
        }

        /// <summary>D23 SENDER. The live offer (GameInstance.cs:185 `healthInsurancePlanOffers`) and the HR
        /// plan it is for (HealthInsurancePlanOffer.cs:13 `hrManagerPlanId`), as the wire carries them. The
        /// headquarters key is what the ACCEPT route is addressed at, so a copy without one is relayed
        /// without buttons rather than with buttons that could not route.</summary>
        private static void FillInsuranceTerms(CompanyMessagePayload p)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.healthInsurancePlanOffers == null) return;
                Entities.HealthInsurancePlanOffer offer = null;
                foreach (var o in gi.healthInsurancePlanOffers) if (o != null && o.id == p.CtxOfferId) { offer = o; break; }
                if (offer == null) return;
                p.CtxPlanId        = offer.hrManagerPlanId ?? "";
                p.CtxPlanType      = (int)offer.planType;
                p.CtxOfferPrice    = offer.initialOfferPrice;
                p.CtxOfferMinPrice = offer.minOfferPrice;
                foreach (var pl in gi.hrManagerPlans ?? new List<Buildings.Office.Headquarters.HrManagerPlan>())
                    if (pl != null && pl.id == p.CtxPlanId)
                    { try { p.CtxHqKey = GameStateReader.AddressKey(pl.headquartersAddress); } catch { } break; }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} insurance terms: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Is this contact one THIS machine really owns? A business contact is matched by name
        /// against my registrations and gated on MergerFlip.TrulyMine (the presentation flip is not
        /// ownership); a person's contact is matched against my own employee and candidate records, with the
        /// injected copies excluded so a partner's person never relays from here.</summary>
        private static string OwnedContactAddress(Contact contact, out string why, out string personId)
        {
            why = null; personId = "";
            var gi = SaveGameManager.Current;
            if (gi == null) { why = "no save"; return ""; }
            string name = contact.id ?? "";
            if (name.Length == 0) { why = "no name"; return ""; }

            if (gi.BuildingRegistrations != null)
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    string bn; try { bn = reg.BusinessName; } catch { continue; }
                    if (!string.Equals(bn, name, StringComparison.Ordinal)) continue;
                    if (!MergerFlip.TrulyMine(reg)) { why = "that business is a partner's"; return ""; }
                    try { return GameStateReader.AddressKey(reg); } catch { return ""; }
                }

            var person = FindOwnPerson(gi, name);
            if (person == null) { why = "no business or person of mine has that name"; return ""; }
            personId = person.id ?? "";
            try { return person.assignedAddress != null ? GameStateReader.AddressKey(person.assignedAddress) : ""; }
            catch { return ""; }
        }

        internal const string CampaignFinishedKey = "ba:messagetype_phone_recruitment_agency_campaign_finished_info";

        /// <summary>P3/P4.  Two message families belong to the whole company yet arrive on a contact this
        /// machine cannot own, so OwnedContactAddress always refuses them.  They are gated on the KEY.
        ///   (a) RECRUITMENT (P3).  RecruitmentCampaign.FinishCampaign (decompile Entities/RecruitmentCampaign.cs:74-89)
        ///       addresses the AGENCY - a service business nobody owns.  The campaign's own `businessAddress`
        ///       (:30) is the shop it hires for, and THAT is the owned address the copy travels under, so the
        ///       member's contact row and header take the sending member's colour through the same
        ///       OwnerOfRelayContact route every other relayed contact takes.  The campaign is still in
        ///       RecruitmentCampaigns while it sends (RecruitmentHelper.RunHourly:38 removes the finished ones
        ///       only AFTER the FinishCampaign pass at :36), and campaigns are per-machine save state, so
        ///       exactly ONE machine ever raises it - no dedupe beyond the never-relay-a-relay rule above.
        ///   (b) RIVAL NEWS (P4).  The three special messages of BigAmbitions.Rivals/RivalDefenseHelper (:100,
        ///       :151, :191) are drained by RivalTimeline.CompleteEntry (:245-249) onto the RIVAL's contact
        ///       through this very gateway, carrying isSpecialMessage - which the payload already has a field
        ///       for (IsSpecial), so the copy arrives special too.  The whole rival timeline is HOST-ONLY in
        ///       this mod (Patch_RivalsHelper_CheckRivalTimelines_SkipOnClient, MPPatches.cs:1656), so the host
        ///       is the only machine that can raise them and a member can never raise a duplicate.  It is
        ///       world news about no one's address, so it travels with an empty address key.
        /// Returns the address key to travel under, or null with a reason.</summary>
        private static string ForeignContactRelayAddress(Contact contact, TextMessage msg, out string why)
        {
            why = null;
            string key = msg.messageKey ?? "";

            if (key == CampaignFinishedKey)
            {
                var gi = SaveGameManager.Current;
                var camps = gi?.RecruitmentCampaigns;
                if (camps == null || camps.Count == 0) { why = "no recruitment campaign here"; return null; }
                Address agency = null;
                try { agency = contact.Address; } catch { }
                if (agency == null) { why = "that agency contact has no address"; return null; }
                // POPUPS-1 review MAJOR-1: several campaigns can share one agency; the notice belongs to the one
                // that has just FINISHED (RecruitmentHelper.cs:34-37 calls FinishCampaign inside the ForEach and
                // only then RemoveAll, so `finished` is still true here). The first campaign at the agency is
                // taken only when none is marked finished. MINOR-2: fail CLOSED - a shop this machine cannot
                // resolve is never relayed as mine (TrulyMine(null) is false).
                Entities.RecruitmentCampaign pick = null;
                foreach (var c in camps)
                {
                    if (c == null || c.agencyAddress == null || c.businessAddress == null) continue;
                    if (!(c.agencyAddress == agency)) continue;
                    bool fin = false; try { fin = c.finished; } catch { }
                    if (fin) { pick = c; break; }
                    if (pick == null) pick = c;
                }
                if (pick == null) { why = "no campaign of mine at that agency"; return null; }
                {
                    string shopKey;
                    try { shopKey = GameStateReader.AddressKey(pick.businessAddress); } catch { why = "that campaign's shop has no key"; return null; }
                    var reg = GameStatePatcher.FindRegistration(shopKey);
                    if (!MergerFlip.TrulyMine(reg))
                    { why = reg == null ? "that campaign's shop is not known here" : "that campaign hires for a partner's shop"; return null; }
                    return shopKey;
                }
            }

            if (key == "ba:messagetype_impacted_products"
             || key == "ba:messagetype_rivals_businesses_opened"
             || key == "ba:messagetype_rivals_attempting_to_poach")
            {
                if (!MPServer.IsRunning) { why = "rival news is raised on the host only"; return null; }
                return "";
            }

            return null;
        }

        private static EmployeeInstance FindOwnPerson(GameInstance gi, string name)
        {
            try
            {
                if (gi.EmployeeInstances != null)
                    foreach (var e in gi.EmployeeInstances)
                    {
                        if (e == null || e.characterData == null) continue;
                        if (!string.Equals(e.characterData.name, name, StringComparison.Ordinal)) continue;
                        bool inj = false; try { inj = MPRegisterSync.IsInjectedStaff(e.id); } catch { }
                        if (!inj) return e;
                    }
                if (gi.CandidateEmployeeInstances != null)
                    foreach (var c in gi.CandidateEmployeeInstances)
                    {
                        if (c == null || c.characterData == null) continue;
                        if (!string.Equals(c.characterData.name, name, StringComparison.Ordinal)) continue;
                        if (CompanyCandidates.IsInjectedCandidate(c.id)) continue;
                        return c;
                    }
            }
            catch { }
            return null;
        }

        private static void Send(CompanyMessagePayload p)
        {
            if (MPServer.IsRunning) MPServer.HostRouteCompanyMessages(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CompanyMessages, MPConfig.PlayerId, p));
        }

        // -- C3 RECEIVER --

        /// <summary>MAIN THREAD. The three inbound legs, from the host.</summary>
        public static void Receive(CompanyMessagePayload p)
        {
            try
            {
                if (!PayloadSane(p, "receiver")) return;
                switch (p.Action)
                {
                    case "msg":     ApplyRelayed(p); return;
                    case "press":   RunPressHere(p); return;
                    // r2 MAJOR-1: NEVER inline. A press travels from inside the game's own click delegate
                    // (ContextButton.cs:72-78), and on a HOST that is the presser the whole round trip -
                    // Send -> HostRouteCompanyMessages -> HostRefusePress -> Receive - is one synchronous
                    // stack. Putting the descriptors back there put them back BEFORE the delegate's own
                    // `messageData?.contextButtonData?.Clear()` (:77) wiped them again: buttons gone,
                    // nothing pending, no retry. On a later frame that Clear has already happened.
                    case "handled": GameStatePatcher.EnqueueOnMainThread(() => ApplyHandled(p)); return;
                    case "refused": GameStatePatcher.EnqueueOnMainThread(() => ApplyRefused(p)); return;
                    default:
                        Plugin.Logger.LogWarning($"{Tag} unknown action '{p.Action}' from '{p.PlayerId}' - ignored.");
                        return;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} Receive: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Re-raise a partner's message through the game's own path: GetContact auto-creates the
        /// contact exactly as a native first message does, and SendMessage drives the badge and the toast.
        /// The copy's contextAction is DROPPED on purpose - it is the only thing that could make deleting
        /// this copy discard the owner's candidate or insurance offer.</summary>
        private static void ApplyRelayed(CompanyMessagePayload p)
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return;
            if (string.IsNullOrEmpty(p.OwnerPid) || p.OwnerPid == MPConfig.PlayerId) return;
            if (!MergerSync.MergedRuntime(p.OwnerPid, MPConfig.PlayerId))
            {
                if (_logged.Add("nomember|" + p.OwnerPid))
                    Plugin.Logger.LogInfo($"{Tag} a message from '{p.OwnerPid}' arrived but we are not in one company - ignored.");
                return;
            }
            if (_copies.ContainsKey(p.MessageId)) return;                      // a resend

            var ad = new AdditionalMessageData { contextButtonData = new List<TextMessage.ContextButtonData>() };
            string mid = p.MessageId;

            // r4 MINOR-2: this id is ALREADY marked handled here - the owner's 'handled' leg arrived while
            // this machine held no copy of it (the mark is added before the _copies lookup on purpose, so a
            // handled message stays handled even when no copy is here). Drawing buttons on the late copy
            // would draw buttons the local press then refuses as 'already handled', so it arrives in the
            // state a handled copy ends in: read, with no buttons and no notification.
            bool lateHandled = _handled.Contains(mid);

            if (!lateHandled)
            {
                foreach (var b in p.Buttons ?? new List<RelayButton>())
                {
                    int idx = ad.contextButtonData.Count;
                    var colour = Enum.IsDefined(typeof(ContextButton.BackgroundColor), b.Colour)
                               ? (ContextButton.BackgroundColor)b.Colour : ContextButton.BackgroundColor.gray;
                    ad.contextButtonData.Add(new TextMessage.ContextButtonData(b.Key, colour, () => SendPress(mid, idx)));
                }

                // The salary negotiation opens HERE, on the shared-pool copy, through build A's claim - the one
                // claim there is. The label is the game's own "negotiate" key, so no word is invented.
                string candidateId = p.CtxEmployeeId ?? "";
                if (p.CtxType == (int)TextMessage.ContextAction.ContextActionType.SalaryNegotiation && candidateId.Length > 0)
                    ad.contextButtonData.Add(new TextMessage.ContextButtonData("dialog_negotiate_button",
                        ContextButton.BackgroundColor.orange, () => OpenLocalNegotiation(mid, candidateId)));
                // D23 (part 2a): the staff-insurance offer is ACTIONABLE here too. The three labels are the
                // game's own keys, built exactly as ContactsApp.InitHealthInsuranceButtons builds them
                // (decompile :603-634) - no new on-screen text. DECLINE and ACCEPT route the commit to the
                // runner of the HR plan's headquarters; NEGOTIATE opens the game's own dialog LOCALLY under
                // the company claim lock, and its own accept routes through the same leg.
                else if (p.CtxType == (int)TextMessage.ContextAction.ContextActionType.HealthInsurancePlanOffer
                         && (p.CtxOfferId ?? "").Length > 0 && (p.CtxPlanId ?? "").Length > 0
                         && (p.CtxHqKey ?? "").Length > 0)
                {
                    _insurance[mid] = new InsuranceCopy
                    {
                        OfferId = p.CtxOfferId, PlanId = p.CtxPlanId, HqKey = p.CtxHqKey,
                        PlanType = p.CtxPlanType, Price = p.CtxOfferPrice, MinPrice = p.CtxOfferMinPrice,
                        OwnerPid = p.OwnerPid ?? "",
                    };
                    ad.contextButtonData.Add(new TextMessage.ContextButtonData("dialog_decline_button",
                        ContextButton.BackgroundColor.gray, () => CommitInsurance(mid, false, 0f)));
                    ad.contextButtonData.Add(new TextMessage.ContextButtonData("dialog_negotiate_button",
                        ContextButton.BackgroundColor.orange, () => OpenLocalInsuranceNegotiation(mid)));
                    ad.contextButtonData.Add(new TextMessage.ContextButtonData("dialog_accept_button",
                        ContextButton.BackgroundColor.blue, () => CommitInsurance(mid, true, p.CtxOfferPrice)));
                    Plugin.Logger.LogInfo($"{Tag} {mid}: the health-insurance offer on '{p.ContactId}' is actionable here - its commit routes to whoever runs '{p.CtxHqKey}' (plan {p.CtxPlanId}).");
                }
                else if (p.CtxType == (int)TextMessage.ContextAction.ContextActionType.HealthInsurancePlanOffer)
                    Plugin.Logger.LogWarning($"{Tag} {mid}: the health-insurance offer on '{p.ContactId}' arrived without its plan or headquarters - shown without actions (nothing could be routed).");
            }

            var msg = new TextMessage(p.MessageKey,
                                      p.Data == null ? new Dictionary<string, string>() : new Dictionary<string, string>(p.Data),
                                      read: lateHandled, isNewInteraction: p.IsNewInteraction, isSpecialMessage: p.IsSpecial,
                                      additionalData: ad.contextButtonData.Count > 0 ? ad : null);
            msg.contextAction = null;                                          // THE DELETE GUARD (see the class note)

            Address addr = null;
            try { if (!string.IsNullOrEmpty(p.StreetName) && p.StreetNumber != 0) addr = new Address(p.StreetName, p.StreetNumber); } catch { }

            Contact contact = null;
            bool created = false;
            try
            {
                string desc = p.ContactDescription ?? "";
                if (gi.Contacts != null)
                    foreach (var c in gi.Contacts)
                        if (c != null && c.id == p.ContactId && c.description == desc) { contact = c; break; }
                created = contact == null;
                // skipNewNotification: a partner's contact appearing is not an event THIS player caused.
                contact = Contact.GetContact(p.ContactId, (ContactCategoryName)p.ContactCategory, desc, addr,
                                             hasWelcomeMessages: false, skipNewNotification: true);
            }
            catch (Exception cx) { Plugin.Logger.LogWarning($"{Tag} {mid}: contact '{p.ContactId}': {cx.GetType().Name}: {cx.Message}"); return; }
            if (contact == null) return;
            if (created) _createdHere.Add(contact);
            // HO-1c L1: remember WHOSE message made this contact appear, so the contacts app can paint its name
            // label in that member's colour (the patches in SharedShopStaff).  The payload's sending member is
            // OwnerPid - the same field Track() stamps the copy with below.
            // POPUPS-1 review MINOR-3: the rival news keys are WORLD news relayed from the host - the rival's
            // contact is nobody's, so it carries no member colour.
            bool worldNews = p.MessageKey == "ba:messagetype_impacted_products"
                          || p.MessageKey == "ba:messagetype_rivals_businesses_opened"
                          || p.MessageKey == "ba:messagetype_rivals_attempting_to_poach";
            if (!string.IsNullOrEmpty(p.OwnerPid) && !worldNews) _relayOwner[contact] = p.OwnerPid;

            // MINOR-4: contact.SendMessage runs CleanOldMessages (decompile Entities/Contact.cs:148-180),
            // which dequeues at 21 and, when the message it evicts is a NATIVE one carrying a contextAction,
            // deletes THIS save's own candidate / insurance offer with it. A copy must never be the reason
            // that happens, so at the cap the oldest COPY gives way first - and when there is none to give,
            // the relay skips this message instead.
            if (!MakeRoomForCopy(contact, mid)) return;

            _applying = true;
            try { contact.SendMessage(msg, p.Notify && !lateHandled, sendNotificationInstantly: false); }
            catch (Exception sx) { Plugin.Logger.LogWarning($"{Tag} {mid}: re-raise: {sx.GetType().Name}: {sx.Message}"); }
            finally { _applying = false; }

            Track(mid, contact, msg, p.OwnerPid);
            if (lateHandled)
                Plugin.Logger.LogInfo($"{Tag} {mid}: the copy arrived for a message already handled here - shown read, with no buttons.");
            Plugin.Logger.LogInfo($"{Tag} {mid}: '{p.MessageKey}' from '{p.OwnerPid}' shown on '{contact.id}' ({ad.contextButtonData.Count} button(s)).");
        }

        /// <summary>A relayed copy's button was pressed here: ask the host to have the OWNER run it.</summary>
        private static void SendPress(string messageId, int buttonIndex)
        {
            try
            {
                if (!_copies.TryGetValue(messageId, out var c))
                { Plugin.Logger.LogWarning($"{Tag} press of '{messageId}' - this machine no longer holds that copy; nothing sent."); return; }
                if (_pending.ContainsKey(messageId))
                { Plugin.Logger.LogInfo($"{Tag} press of '{messageId}' ignored - a press is already in flight; the owner answers the first one."); return; }
                if (_handled.Contains(messageId))
                { Plugin.Logger.LogInfo($"{Tag} press of '{messageId}' ignored - it has already been handled."); return; }

                // MAJOR-2: the game wipes this copy's own button list the instant this click returns
                // (decompile ContextButton.cs:77), so the descriptors are kept here until the owner's
                // 'handled' or a 'refused' resolves the press. A refusal puts them back on the copy.
                var live = c.msg?.additionalData?.contextButtonData;
                var kept = new List<TextMessage.ContextButtonData>();
                if (live != null) foreach (var b in live) kept.Add(b);
                _pending[messageId] = new PendingPress { Kept = kept, ButtonIndex = buttonIndex, LastSent = NowSeconds() };

                Send(new CompanyMessagePayload
                {
                    PlayerId = MPConfig.PlayerId, Action = "press", MessageId = messageId,
                    OwnerPid = c.owner, ButtonIndex = buttonIndex,
                });
                Plugin.Logger.LogInfo($"{Tag} press of button {buttonIndex} on {messageId} sent - '{c.owner}' runs it once and tells the company who handled it.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} SendPress: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>THE OWNER: run the ORIGINAL closure, exactly once, and tell the company.</summary>
        private static void RunPressHere(CompanyMessagePayload p)
        {
            string mid = p.MessageId;
            if (!_mine.TryGetValue(mid, out var m))
            { Plugin.Logger.LogWarning($"{Tag} press of '{mid}' by '{p.PlayerId}' REFUSED - this machine does not hold that message any more."); RefusePress(p, "gone"); return; }
            if (_handled.Contains(mid))
            { Plugin.Logger.LogWarning($"{Tag} press of '{mid}' by '{p.PlayerId}' REFUSED - it has already been handled."); RefusePress(p, "handled"); return; }
            var list = m.msg?.additionalData?.contextButtonData;
            if (list != null && list.Count == 0)
            {
                // r2 MAJOR-2 belt. The list is EMPTY, which on the owner means the game itself wiped it when
                // this message was answered on THIS machine's own phone (ContextButton.cs:77) - before the
                // native-press hook below existed, or by any other clear. Answering 'nobutton' would send the
                // descriptors back to the presser and the same press would come round for ever. It DID run.
                _handled.Add(mid);
                try { m.msg.read = true; } catch { }
                Plugin.Logger.LogWarning($"{Tag} press of '{mid}' by '{p.PlayerId}' - this message was already answered on this machine's own phone; the company is told 'handled' instead.");
                Send(new CompanyMessagePayload
                {
                    PlayerId = MPConfig.PlayerId, Action = "handled", MessageId = mid,
                    OwnerPid = MPConfig.PlayerId, HandledBy = MPConfig.PlayerId, ButtonIndex = p.ButtonIndex,
                });
                return;
            }
            if (list == null || p.ButtonIndex < 0 || p.ButtonIndex >= list.Count)
            { Plugin.Logger.LogWarning($"{Tag} press of '{mid}' by '{p.PlayerId}' REFUSED - button {p.ButtonIndex} is not on that message here."); RefusePress(p, "nobutton"); return; }
            var cb = list[p.ButtonIndex];
            if (cb.onClick == null)
            { Plugin.Logger.LogWarning($"{Tag} press of '{mid}' by '{p.PlayerId}' REFUSED - button {p.ButtonIndex} carries no action on this machine."); RefusePress(p, "nobutton"); return; }

            _handled.Add(mid);
            try { cb.onClick(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} {mid}: running button {p.ButtonIndex} for '{p.PlayerId}': {ex.GetType().Name}: {ex.Message}"); }
            // What the game itself does after a press (ContextButton.SetUp): the buttons go and the message
            // is done with. Marking it read is the game's own read state - nothing new on screen.
            try { list.Clear(); } catch { }
            try { m.msg.read = true; } catch { }
            RedrawIfOnScreen(m.contact, mid);                                  // MAJOR-1: the DRAWN buttons too
            Plugin.Logger.LogInfo($"{Tag} {mid}: button {p.ButtonIndex} run here - handled by '{p.PlayerId}'.");
            Send(new CompanyMessagePayload
            {
                PlayerId = MPConfig.PlayerId, Action = "handled", MessageId = mid,
                OwnerPid = MPConfig.PlayerId, HandledBy = p.PlayerId ?? "", ButtonIndex = p.ButtonIndex,
            });
        }

        /// <summary>Every other copy: the buttons go and the message is read - the game's own two states.</summary>
        private static void ApplyHandled(CompanyMessagePayload p)
        {
            string mid = p.MessageId;
            _handled.Add(mid);
            _pending.Remove(mid);                                              // the press is answered
            if (!_copies.TryGetValue(mid, out var c)) return;
            try { c.msg?.additionalData?.contextButtonData?.Clear(); } catch { }
            try { if (c.msg != null) c.msg.read = true; } catch { }
            try { InstanceBehavior<UI.UIs>.Instance?.smartphoneUI?.UpdateBadgeCount(AppName.Contacts, false); } catch { }
            RedrawIfOnScreen(c.contact, mid);                                  // MAJOR-1: the same on a copy
            Plugin.Logger.LogInfo($"{Tag} {mid}: handled by '{p.HandledBy}' - this copy is read and its buttons are done.");
        }

        /// <summary>MAJOR-2. The press this machine sent did not run: the local click had already cleared the
        /// copy's buttons (decompile ContextButton.cs:77), so without this leg the action is silently lost and
        /// a real click can never retry. The kept descriptors go back, the copy is unread again, and the
        /// conversation is redrawn if it is open. The one reason that is NOT retryable is "handled" - the
        /// action really did run, so that copy resolves exactly as the 'handled' leg resolves it.</summary>
        private static void ApplyRefused(CompanyMessagePayload p)
        {
            string mid = p.MessageId;
            if (!string.IsNullOrEmpty(p.TargetPid) && p.TargetPid != MPConfig.PlayerId) return;
            _pending.TryGetValue(mid, out var pend);
            _pending.Remove(mid);
            string why = ReasonWords(p.Reason);
            if (!_copies.TryGetValue(mid, out var c))
            { Plugin.Logger.LogInfo($"{Tag} press refused by the host ({why}) - this machine no longer holds '{mid}', so there is nothing to put back."); return; }

            if (p.Reason == "handled")
            {
                _handled.Add(mid);
                try { c.msg?.additionalData?.contextButtonData?.Clear(); } catch { }
                try { if (c.msg != null) c.msg.read = true; } catch { }
                RedrawIfOnScreen(c.contact, mid);
                Plugin.Logger.LogInfo($"{Tag} {mid}: press refused by the host ({why}) - the buttons stay gone, because the action did run.");
                return;
            }

            try
            {
                var ad = c.msg?.additionalData;
                var kept = pend?.Kept;
                if (ad?.contextButtonData != null && kept != null && kept.Count > 0)
                {
                    ad.contextButtonData.Clear();
                    foreach (var b in kept) ad.contextButtonData.Add(b);
                }
                _handled.Remove(mid);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} {mid}: putting the buttons back: {ex.GetType().Name}: {ex.Message}"); }
            RedrawIfOnScreen(c.contact, mid);
            // r2 MINOR-4: the unread mark goes on AFTER the redraw. ShowContactConversation runs the game's
            // own contact.ReadAllMessages (decompile ContactsApp.cs:342), so setting read=false first was
            // undone in the same call whenever that conversation was the one on screen.
            try
            {
                if (c.msg != null) c.msg.read = false;
                InstanceBehavior<UI.UIs>.Instance?.smartphoneUI?.UpdateBadgeCount(AppName.Contacts, false);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} {mid}: marking the copy unread again: {ex.GetType().Name}: {ex.Message}"); }
            Plugin.Logger.LogInfo($"{Tag} {mid}: press refused by the host ({why}) - the buttons are back.");
        }

        /// <summary>The wire carries a short code; the log carries words. No new ON-SCREEN text either way.</summary>
        private static string ReasonWords(string code) => code switch
        {
            "offline"  => "the owner is not online",
            "gone"     => "the owner no longer holds that message",
            "handled"  => "it had already been handled there",
            "nobutton" => "that button is not on the owner's message",
            "busy"     => "the host is rate-limiting this player just now",
            _          => "the host does not know that message",
        };

        /// <summary>THE OWNER answering a press it could not run: the presser is told so its copy offers the
        /// button again. Our OWN press needs no wire leg - the local log is the answer.</summary>
        private static void RefusePress(CompanyMessagePayload p, string reason)
        {
            try
            {
                string to = p.PlayerId ?? "";
                if (to.Length == 0 || to == MPConfig.PlayerId) return;
                Send(new CompanyMessagePayload
                {
                    PlayerId = MPConfig.PlayerId, Action = "refused", MessageId = p.MessageId ?? "",
                    OwnerPid = MPConfig.PlayerId, TargetPid = to, Reason = reason, ButtonIndex = p.ButtonIndex,
                });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} refusing a press: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static System.Reflection.MethodInfo _showConversation;

        /// <summary>MAJOR-1. Clearing the MODEL's button list does not touch what is already DRAWN: the
        /// rendered listener closes over its OWN copy of the descriptor (ContextButtonData is a struct,
        /// decompile Entities/TextMessage.cs:117), so a conversation left open still offers a live button and
        /// one more click runs the action a SECOND time. The game's own redraw of the open conversation is
        /// ContactsApp.ShowContactConversation (decompile UI.Smartphone.Apps.Contacts/ContactsApp.cs:323-351
        /// -> ResetMessages + ShowContactMessages), which rebuilds every entry from the model - so the button
        /// is simply not drawn again. It is private, hence the reflection; it starts a coroutine, so it needs
        /// the MAIN THREAD and an active object. (SetContextButtonsNoninteractable is not used: its static
        /// ContextButtons list is pruned only for the GROUP a conversation rebuilds, decompile
        /// ContactsApp.cs:718, and otherwise cleared only at scene reset, :40/:147 - so it still holds
        /// destroyed buttons from every earlier render of every OTHER conversation.)</summary>
        private static void RedrawIfOnScreen(Contact contact, string mid)
        {
            try
            {
                if (contact == null) return;
                var app = InstanceBehavior<UI.UIs>.Instance?.fullMenu?.contactsApp;
                if (app == null || !ReferenceEquals(app.selectedContact, contact)) return;   // not on screen
                if (!app.isActiveAndEnabled) return;                                          // cannot start a coroutine
                _showConversation ??= AccessTools.Method(typeof(ContactsApp), "ShowContactConversation", new[] { typeof(Contact) });
                if (_showConversation == null)
                { Plugin.Logger.LogWarning($"{Tag} {mid}: this build has no ShowContactConversation - the drawn buttons stay until the conversation is reopened."); return; }
                _showConversation.Invoke(app, new object[] { contact });
                Plugin.Logger.LogInfo($"{Tag} {mid}: the open conversation with '{contact.id}' was redrawn - what is drawn matches the message again.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} {mid}: conversation redraw: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>MINOR-4. The receiver's contact is at the game's 20-message cap and the message the game
        /// would evict is one of THIS save's own records: give up the oldest relayed copy instead, or say so
        /// and let the copy stay away. Returns false when the relay must skip this message.</summary>
        private static bool MakeRoomForCopy(Contact contact, string mid)
        {
            try
            {
                var q = contact?.messagesQueue;
                if (q == null || q.Count < 20) return true;
                TextMessage oldest = null;
                foreach (var m in q) { oldest = m; break; }
                if (oldest == null || oldest.contextAction == null) return true;   // the game's own eviction costs nothing
                string victimId = null; TextMessage victim = null;
                foreach (var m in q)
                {
                    foreach (var kv in _copies)
                        if (ReferenceEquals(kv.Value.msg, m)) { victimId = kv.Key; victim = m; break; }
                    if (victim != null) break;
                }
                if (victim == null)
                {
                    Plugin.Logger.LogWarning($"{Tag} {mid}: '{contact.id}' is full and the message the game would drop is one of THIS save's own records - the relay skips this message rather than spend that budget.");
                    return false;
                }
                RemoveFromQueue(contact, victim);
                Forget(victimId);
                Plugin.Logger.LogInfo($"{Tag} {mid}: '{contact.id}' was full, so the oldest relayed copy ({victimId}) made way - a native message with a context action is never pushed out by a copy.");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} {mid}: making room on '{contact?.id}': {ex.GetType().Name}: {ex.Message}"); return true; }
        }

        /// <summary>The relayed salary negotiation, opened HERE on the company's own candidate. The game's
        /// own MyEmployees.NegotiateWithCandidate is the entry, so build A's claim prefix decides: the host
        /// grants the first asker, the accept stays host-gated, and nothing new arbitrates anything.</summary>
        // ── D23: THE RELAYED STAFF-INSURANCE OFFER (4c part 2a, E3) ──────────────────────────────
        //
        // The offer and the HR plan live in the OWNER's save; this machine only ever holds a copy of the
        // message. So the three actions split in two: NEGOTIATE runs the game's own dialog HERE, under the
        // company claim lock (one member at a time, arbitrated by the host on the candidate claim table,
        // keyed on the OFFER id) - and the moment of COMMITMENT, whichever path reaches it, becomes a
        // `mergerplanedit` leg to the runner of the HR plan's headquarters, which calls the game's own
        // HealthInsurancePlanOffer.AcceptOffer / DeclineOffer on the REAL offer. Nothing is committed here.

        private sealed class InsuranceCopy
        {
            internal string OfferId = "", PlanId = "", HqKey = "", OwnerPid = "";
            internal int PlanType;
            internal float Price, MinPrice;
        }

        /// <summary>message id -> the relayed offer's terms, kept for as long as the copy is on screen.</summary>
        private static readonly Dictionary<string, InsuranceCopy> _insurance = new();

        /// <summary>offer id -> the message it came on, so a granted claim finds its way back.</summary>
        private static readonly Dictionary<string, string> _insuranceByOffer = new();

        /// <summary>r2 MINOR-8: whose offer this is - what a claim RELEASE has to be addressed to when the
        /// negotiation dialog is closed without a commit. "" when this machine holds no copy of it.</summary>
        public static string OwnerOfOffer(string offerId)
        {
            try
            {
                if (string.IsNullOrEmpty(offerId) || !_insuranceByOffer.TryGetValue(offerId, out var mid) || mid == null) return "";
                return _insurance.TryGetValue(mid, out var c) && c != null ? (c.OwnerPid ?? "") : "";
            }
            catch { return ""; }
        }

        /// <summary>ACCEPT / DECLINE, from the copy's own button or from the game's negotiation dialog.
        /// One commit per message: the id is marked handled here and the company is told, so every other
        /// copy loses its buttons through the relay's existing `handled` leg.</summary>
        internal static void CommitInsurance(string messageId, bool accept, float price)
        {
            try
            {
                if (!_insurance.TryGetValue(messageId, out var c) || c == null)
                { Plugin.Logger.LogWarning($"{Tag} {messageId}: insurance commit with nothing held here - ignored."); return; }
                if (_handled.Contains(messageId))
                { Plugin.Logger.LogInfo($"{Tag} {messageId}: insurance already handled in this company - no second commit."); return; }
                CompanyPlans.RouteInsurance(accept ? "insurance-accept" : "insurance-decline",
                                           c.PlanId, c.HqKey, c.OfferId, price);
                _handled.Add(messageId);
                CompanyCandidates.ReleaseOfferClaim(c.OfferId, c.OwnerPid);
                Send(new CompanyMessagePayload
                {
                    PlayerId = MPConfig.PlayerId, Action = "handled", MessageId = messageId,
                    OwnerPid = c.OwnerPid, HandledBy = MPConfig.PlayerId, ButtonIndex = -1,
                });
                GameStatePatcher.EnqueueOnMainThread(() => ApplyHandled(new CompanyMessagePayload
                { PlayerId = MPConfig.PlayerId, Action = "handled", MessageId = messageId, OwnerPid = c.OwnerPid, HandledBy = MPConfig.PlayerId }));
                Plugin.Logger.LogInfo($"{Tag} insurance {(accept ? "accept" : "decline")} routed for plan '{c.PlanId}' at '{c.HqKey}' (offer {c.OfferId}, message {messageId}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} insurance commit: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>NEGOTIATE on a relayed copy: ask the host for the claim on this OFFER first (the same
        /// first-asker arbitration build A gave candidates), then open the game's own dialog here.</summary>
        private static void OpenLocalInsuranceNegotiation(string messageId)
        {
            try
            {
                if (!_insurance.TryGetValue(messageId, out var c) || c == null) return;
                if (_handled.Contains(messageId))
                { Plugin.Logger.LogInfo($"{Tag} {messageId}: the insurance offer is already handled in this company - nothing opened."); return; }
                _insuranceByOffer[c.OfferId] = messageId;
                CompanyCandidates.ClaimOffer(c.OfferId, c.OwnerPid);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} insurance negotiate: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>MAIN THREAD. The host granted this machine the claim on that offer: open the game's own
        /// HealthInsuranceNegotiationDialog on a DETACHED copy of the offer. The dialog only reads the
        /// offer's numbers (Dialogs/HealthInsuranceNegotiationDialog.cs:24-27) and calls AcceptOffer /
        /// DeclineOffer on it, both of which the merger gate turns into the routed commit.</summary>
        public static void OfferClaimGranted(string offerId)
        {
            try
            {
                if (!_insuranceByOffer.TryGetValue(offerId ?? "", out var mid) || mid == null) return;
                if (!_insurance.TryGetValue(mid, out var c) || c == null) return;
                var app = UnityEngine.Object.FindObjectOfType<UI.Smartphone.Apps.Contacts.ContactsApp>(true);
                if (app == null)
                { Plugin.Logger.LogWarning($"{Tag} {mid}: the insurance negotiation cannot open - the contacts app is not on screen."); return; }

                var offer = new Entities.HealthInsurancePlanOffer(c.PlanId, (Entities.HealthInsurancePlanType)c.PlanType)
                { initialOfferPrice = c.Price, minOfferPrice = c.MinPrice };
                SetReadonly(offer, "id", c.OfferId);
                _relayedOffers[c.OfferId] = mid;

                // ContactsApp.InitHealthInsuranceButtons' own negotiate delegate, step for step
                // (decompile :616-624) - the same fields, set on the same instance.
                SetField(app, "callButton", null, interactableFalse: true);
                var selected = app.selectedContact;
                SetField(app, "contact", selected);
                Dialogs.HealthInsuranceNegotiationDialog.planOffer = offer;
                DialogController.current = app;
                SetField(app, "dialog", Dialogs.CallDialogFactory.GetDialog(Dialogs.CallDialogType.HealthInsuranceNegotiationDialog));
                Plugin.Logger.LogInfo($"{Tag} insurance negotiation opened here for plan '{c.PlanId}' under the company claim on offer '{c.OfferId}'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} insurance dialog: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>offer id -> the message id, for the DETACHED offers this machine minted. The accept /
        /// decline gate uses it to turn the dialog's own commit into the routed one.</summary>
        private static readonly Dictionary<string, string> _relayedOffers = new();

        /// <summary>THE COMMIT GATE. True = this offer is a relayed copy and the commit has been routed
        /// instead of run; the caller must not run the native body (AcceptOffer would dereference
        /// HrManagerPlan, which resolves to null here - the plan is in the owner's save).</summary>
        public static bool RouteOfferCommit(Entities.HealthInsurancePlanOffer offer, bool accept, float price)
        {
            try
            {
                string id = offer != null ? (offer.id ?? "") : "";
                if (id.Length == 0 || !_relayedOffers.TryGetValue(id, out var mid)) return false;
                CommitInsurance(mid, accept, price);
                // The dialog reads these two straight after its own call, so the detached copy ends in the
                // state the game expects; nothing else on it is ever persisted.
                try { offer.negotiationFinished = true; offer.accepted = accept; } catch { }
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} offer commit gate: {ex.GetType().Name}: {ex.Message}"); return false; }
        }

        private static void SetReadonly(object o, string field, object value)
        {
            try
            {
                for (var t = o.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (f != null) { f.SetValue(o, value); return; }
                }
            }
            catch { }
        }

        private static void SetField(object o, string field, object value, bool interactableFalse = false)
        {
            try
            {
                for (var t = o.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (f == null) continue;
                    if (interactableFalse)
                    {
                        var b = f.GetValue(o) as UnityEngine.UI.Button;
                        if (b != null) b.interactable = false;
                        return;
                    }
                    f.SetValue(o, value);
                    return;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} field '{field}': {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void OpenLocalNegotiation(string messageId, string candidateId)
        {
            try
            {
                EmployeeInstance inst = null;
                try { EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(candidateId, out inst); } catch { }
                if (inst == null)
                {
                    var gi = SaveGameManager.Current;
                    if (gi?.CandidateEmployeeInstances != null)
                        foreach (var c in gi.CandidateEmployeeInstances) if (c?.id == candidateId) { inst = c; break; }
                }
                var page = InstanceBehavior<UI.UIs>.Instance?.fullMenu?.myEmployees;
                if (inst == null || page == null)
                {
                    Plugin.Logger.LogWarning($"{Tag} {messageId}: the negotiation cannot open here - '{candidateId}' is not in the company pool on this machine (the origin may have hired or dropped them).");
                    return;
                }
                Plugin.Logger.LogInfo($"{Tag} {messageId}: opening the salary negotiation for '{candidateId}' locally - the claim goes to the host as any other candidate's would.");
                page.NegotiateWithCandidate(inst);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} open negotiation: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>r2 MAJOR-3. A press with no answer is re-asked every PressRetrySeconds: the owner may have
        /// dropped between the host's hand-off and its own run, or the host's rate gate may have dropped it.
        /// A repeat is harmless - an id the owner has already run is answered 'handled', and an id it never
        /// had is answered 'gone' - so either way the entry is cleared instead of standing for ever.</summary>
        private static void RetryPendingPresses()
        {
            if (_pending.Count == 0) return;
            float now = NowSeconds();
            List<string> due = null;
            foreach (var kv in _pending)
                if (now - kv.Value.LastSent >= PressRetrySeconds) (due ??= new List<string>()).Add(kv.Key);
            if (due == null) return;
            foreach (var mid in due)
            {
                if (!_pending.TryGetValue(mid, out var pend)) continue;
                if (_handled.Contains(mid) || !_copies.TryGetValue(mid, out var c))
                { _pending.Remove(mid); continue; }
                pend.LastSent = now;
                Send(new CompanyMessagePayload
                {
                    PlayerId = MPConfig.PlayerId, Action = "press", MessageId = mid,
                    OwnerPid = c.owner, ButtonIndex = pend.ButtonIndex,
                });
                Plugin.Logger.LogInfo($"{Tag} {mid}: the press has gone {(int)PressRetrySeconds} s unanswered - asking '{c.owner}' again.");
            }
        }

        /// <summary>r2 MINOR-6. A copy the game itself dropped - deleted in the conversation, or pushed out by
        /// Contact.CleanOldMessages - is no longer in any contact's queue, so the tables would keep pointing at
        /// it for ever (and the save strip would keep walking it). Tracking follows the queue.</summary>
        private static void PruneVanishedCopies()
        {
            if (_copies.Count == 0) return;
            List<string> gone = null;
            foreach (var kv in _copies)
            {
                var q = kv.Value.contact?.messagesQueue;
                bool present = false;
                if (q != null) foreach (var m in q) if (ReferenceEquals(m, kv.Value.msg)) { present = true; break; }
                if (!present) (gone ??= new List<string>()).Add(kv.Key);
            }
            if (gone == null) return;
            foreach (var id in gone) Forget(id);
            Plugin.Logger.LogInfo($"{Tag} {gone.Count} relayed copy(ies) are no longer in any contact's queue (deleted here, or aged out by the game) - they stop being tracked.");
        }

        /// <summary>r2 MAJOR-2. THE OWNER ANSWERED ITS OWN PHONE. The game's click delegate runs
        /// buttonData.onClick() and then wipes THAT message's own button list (decompile
        /// ContextButton.cs:72-78) - a purely local event, so without this hook no 'handled' ever went out and
        /// every partner copy kept a button that could never run: a press on it found an empty list here,
        /// came back 'nobutton', the descriptors went back, and the cycle repeated for ever. The
        /// AdditionalMessageData the delegate holds IS the one on the tracked TextMessage, so the message is
        /// found by reference; anything not relayed from here is simply ignored.</summary>
        public static void OnNativePress(object messageData)
        {
            try
            {
                if (!(messageData is AdditionalMessageData md)) return;
                if (!MergerSync.IAmMember) return;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                if (_mine.Count == 0) return;
                string mid = null;
                foreach (var kv in _mine)
                    if (ReferenceEquals(kv.Value.msg?.additionalData, md)) { mid = kv.Key; break; }
                if (mid == null) return;                       // not one of the messages this machine relayed
                if (!_handled.Add(mid)) return;                // already answered once
                if (_mine.TryGetValue(mid, out var m)) { try { if (m.msg != null) m.msg.read = true; } catch { } }
                Send(new CompanyMessagePayload
                {
                    PlayerId = MPConfig.PlayerId, Action = "handled", MessageId = mid,
                    OwnerPid = MPConfig.PlayerId, HandledBy = MPConfig.PlayerId, ButtonIndex = -1,
                });
                Plugin.Logger.LogInfo($"{Tag} {mid}: answered here on this machine's own phone - every relayed copy of it is told 'handled'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} native press: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>The id this machine already relayed that exact TextMessage under, or null.</summary>
        private static string FindMineId(TextMessage msg)
        {
            if (msg == null) return null;
            foreach (var kv in _mine) if (ReferenceEquals(kv.Value.msg, msg)) return kv.Key;
            return null;
        }

        private static float NowSeconds()
        {
            try { return Time.realtimeSinceStartup; } catch { return 0f; }
        }

        // -- tracking, strips and lifecycle --

        private static void Track(string id, Contact contact, TextMessage msg, string owner, string person = null)
        {
            if (owner == null) _mine[id] = (contact, msg, msg?.messageKey ?? "", person ?? "");
            else _copies[id] = (owner, contact, msg);
            _order.Add(id);
            while (_order.Count > MaxTracked && _mine.Count + _copies.Count > MaxTracked)
            {
                string old = _order[0];
                _order.RemoveAt(0);
                if (_copies.TryGetValue(old, out var c)) RemoveFromQueue(c.contact, c.msg);
                _mine.Remove(old); _copies.Remove(old); _handled.Remove(old); _pending.Remove(old);
            }
        }

        /// <summary>Stop tracking one message id in every table at once (the queue itself is the caller's).</summary>
        private static void Forget(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            _copies.Remove(id); _mine.Remove(id); _handled.Remove(id); _pending.Remove(id); _order.Remove(id);
        }

        /// <summary>MINOR-6. The promoted records of an absent owner are leaving this machine (the stand-in
        /// hand-back, MergerAbsence.RemovePromoted): their messages stop being MINE, because the stored
        /// closures would run over records that have left this save. A press arriving afterwards is refused
        /// through the 'refused' leg ("the owner no longer holds that message") instead of being swallowed.</summary>
        public static void ForgetMine(ICollection<string> employeeIds, string why)
        {
            try
            {
                if (employeeIds == null || employeeIds.Count == 0 || _mine.Count == 0) return;
                var ids = new HashSet<string>(employeeIds, StringComparer.Ordinal);
                var drop = new List<string>();
                foreach (var kv in _mine)
                    if (!string.IsNullOrEmpty(kv.Value.person) && ids.Contains(kv.Value.person)) drop.Add(kv.Key);
                foreach (var id in drop) Forget(id);
                if (drop.Count > 0)
                    Plugin.Logger.LogInfo($"{Tag} {drop.Count} relayed message(s) stopped being mine ({why}) - a press on a copy of them is refused from here on.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} ForgetMine: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static bool IsRelayedCopy(TextMessage msg)
        {
            if (msg == null) return false;
            foreach (var kv in _copies) if (ReferenceEquals(kv.Value.msg, msg)) return true;
            return false;
        }

        private static void RemoveFromQueue(Contact contact, TextMessage msg)
        {
            try
            {
                if (contact?.messagesQueue == null || msg == null) return;
                var keep = new Queue<TextMessage>();
                foreach (var m in contact.messagesQueue) if (!ReferenceEquals(m, msg)) keep.Enqueue(m);
                contact.messagesQueue = keep;
            }
            catch { }
        }

        /// <summary>BOTH SAVE CHOKE POINTS (through MPRegisterSync.StripSyntheticsForSave): a partner's
        /// message is a display copy and never reaches a .hsg. The whole original queue is stashed and put
        /// back after serialisation - the main thread is blocked through the save, so nothing can enqueue
        /// into it in that window. A contact THIS relay created, and that keeps nothing of its own, leaves
        /// the contact list for the same window.</summary>
        public static Action StripForSave(string when)
        {
            var stashed = new List<(Contact c, Queue<TextMessage> original)>();
            var pulled = new List<Contact>();
            try
            {
                if (_copies.Count == 0 && _createdHere.Count == 0) return () => { };
                var gi = SaveGameManager.Current;
                if (gi?.Contacts == null) return () => { };
                var byContact = new Dictionary<Contact, List<TextMessage>>();
                foreach (var kv in _copies)
                {
                    var c = kv.Value.contact; var m = kv.Value.msg;
                    if (c == null || m == null) continue;
                    if (!byContact.TryGetValue(c, out var l)) { l = new List<TextMessage>(); byContact[c] = l; }
                    l.Add(m);
                }
                foreach (var kv in byContact)
                {
                    var c = kv.Key;
                    if (c.messagesQueue == null) continue;
                    var keep = new Queue<TextMessage>();
                    int lifted = 0;
                    foreach (var m in c.messagesQueue)
                    {
                        bool relayed = false;
                        foreach (var r in kv.Value) if (ReferenceEquals(r, m)) { relayed = true; break; }
                        if (relayed) lifted++; else keep.Enqueue(m);
                    }
                    if (lifted > 0)
                    {
                        stashed.Add((c, c.messagesQueue));
                        c.messagesQueue = keep;
                    }
                    if (keep.Count == 0 && _createdHere.Contains(c) && gi.Contacts.Remove(c)) pulled.Add(c);
                }
                // MINOR-5: a contact this relay created whose copies have ALL been deleted or evicted holds
                // nothing of its own - an empty contact named after a partner's business or person - and it
                // is not in the table above, so without this it is the one piece of the relay a .hsg keeps.
                foreach (var c in _createdHere)
                {
                    if (c == null || byContact.ContainsKey(c)) continue;
                    if (c.messagesQueue != null && c.messagesQueue.Count > 0) continue;
                    if (gi.Contacts.Remove(c)) pulled.Add(c);
                }
                if (stashed.Count > 0 || pulled.Count > 0)
                    Plugin.Logger.LogInfo($"{Tag} {_copies.Count} relayed message(s) across {stashed.Count} contact(s) sit out {when}, with {pulled.Count} emptied relay-created contact(s) (they belong to the machines that raised them).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} save strip ({when}): {ex.GetType().Name}: {ex.Message}"); }

            return () =>
            {
                try
                {
                    var gi = SaveGameManager.Current;
                    foreach (var s in stashed) if (s.c != null) s.c.messagesQueue = s.original;
                    if (gi?.Contacts != null)
                        foreach (var c in pulled) if (!gi.Contacts.Contains(c)) gi.Contacts.Add(c);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} save restore ({when}): {ex.GetType().Name}: {ex.Message}"); }
            };
        }

        public static void ClearAll(string why)
        {
            try
            {
                int n = _copies.Count;
                var gi = SaveGameManager.Current;
                foreach (var kv in _copies) RemoveFromQueue(kv.Value.contact, kv.Value.msg);
                if (gi?.Contacts != null)
                    foreach (var c in _createdHere)
                        if (c != null && (c.messagesQueue == null || c.messagesQueue.Count == 0)) gi.Contacts.Remove(c);
                _copies.Clear(); _createdHere.Clear(); _relayOwner.Clear(); _mine.Clear(); _handled.Clear(); _order.Clear(); _pending.Clear();
                _insurance.Clear(); _insuranceByOffer.Clear(); _relayedOffers.Clear();   // r2 MINOR-9: D23's three tables die with the copies
                if (n > 0) Plugin.Logger.LogInfo($"{Tag} dropped {n} relayed message copy(ies) ({why}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} ClearAll: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static void Reset()
        {
            _mine.Clear(); _copies.Clear(); _handled.Clear(); _createdHere.Clear(); _relayOwner.Clear(); _pending.Clear();
            _order.Clear(); _logged.Clear(); _seq = 0; _applying = false;
            _insurance.Clear(); _insuranceByOffer.Clear(); _relayedOffers.Clear();   // r2 MINOR-9
        }

        // -- TestDrive --

        /// <summary>One line per relayed message this machine holds, oldest first.</summary>
        public static List<string> Readout(int max)
        {
            var outp = new List<string>();
            try
            {
                for (int i = _order.Count - 1; i >= 0 && outp.Count < max; i--)
                {
                    string id = _order[i];
                    if (_copies.TryGetValue(id, out var c))
                        outp.Add($"{id}|copy|origin={c.owner}|contact={c.contact?.id}|key={c.msg?.messageKey}"
                               + $"|buttons={c.msg?.additionalData?.contextButtonData?.Count ?? 0}|read={c.msg?.read}|handled={_handled.Contains(id)}");
                    else if (_mine.TryGetValue(id, out var m))
                        outp.Add($"{id}|mine|origin=mine|contact={m.contact?.id}|key={m.key}"
                               + $"|buttons={m.msg?.additionalData?.contextButtonData?.Count ?? 0}|read={m.msg?.read}|handled={_handled.Contains(id)}");
                }
                outp.Reverse();
            }
            catch { }
            return outp;
        }

        /// <summary>TestDrive lever: press a relayed copy's button exactly as clicking it does. "" = the
        /// verb is answerable; `sent` says whether a press actually left, and `reason` why it did not.</summary>
        public static string CommitPress(string messageId, int buttonIndex, out bool sent, out string reason)
        {
            sent = false; reason = "";
            if (string.IsNullOrEmpty(messageId)) return "no message id";
            if (!_copies.TryGetValue(messageId, out var c)) return "not a relayed copy on this machine";
            if (_handled.Contains(messageId)) { reason = "handled"; return ""; }
            if (_pending.ContainsKey(messageId)) { reason = "in-flight"; return ""; }
            var list = c.msg?.additionalData?.contextButtonData;
            if (list == null || list.Count == 0) { reason = "no-buttons"; return ""; }
            if (buttonIndex < 0 || buttonIndex >= list.Count) return $"button {buttonIndex} is not on that copy";
            SendPress(messageId, buttonIndex);
            sent = _pending.ContainsKey(messageId);
            if (!sent) reason = "in-flight";
            return "";
        }

        /// <summary>TestDrive lever (L7): re-send one of MY OWN messages - the same DTO through the same gate
        /// as the Contact.SendMessage postfix, so nothing here takes a shortcut. The message KEEPS its id (r2
        /// caveat: one TextMessage, one id), so a receiver that already holds the copy treats it as a resend.
        /// "" = answerable; `sent` is false when the gate itself stopped it (not a member, no session).</summary>
        public static string CommitRelay(string messageId, out string newId, out string contactName, out int buttons, out bool sent)
        {
            newId = messageId ?? ""; contactName = ""; buttons = 0; sent = false;
            if (string.IsNullOrEmpty(messageId)) return "no message id";
            if (!_copies.ContainsKey(messageId) && !_mine.ContainsKey(messageId)) return "no such relayed message on this machine";
            if (!_mine.TryGetValue(messageId, out var m)) return "that message is a partner's copy here, not mine to relay";
            if (m.contact == null || m.msg == null) return "that message has no contact on this machine any more";
            contactName = m.contact.id ?? "";
            buttons = m.msg.additionalData?.contextButtonData?.Count ?? 0;
            string fresh;
            try { fresh = RelayNow(m.contact, m.msg, false); }
            catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
            sent = !string.IsNullOrEmpty(fresh);
            if (sent) newId = fresh;
            return "";
        }

        /// <summary>TestDrive lever (N1): relay the NEWEST NATIVE message on one of MY OWN contacts. The id
        /// form above can only reach messages relayed SINCE this connection, so a fixture whose phone filled
        /// before the connect could not be driven at all. The pick: my own business/person contacts only (the
        /// send postfix's own ownership gate), native messages only (never a partner's copy, never the
        /// player's own outgoing line), newest by the game's own timestamp, preferring one that still offers
        /// at least one context button; optionally restricted to one contact. It goes out through RelayNow, so
        /// every gate re-runs and an already-tracked message keeps its id instead of being tracked twice.</summary>
        public static string CommitRelayNative(string contactFilter, out string newId, out string contactName,
                                               out string messageKey, out int buttons, out bool sent)
        {
            newId = ""; contactName = ""; messageKey = ""; buttons = 0; sent = false;
            var gi = SaveGameManager.Current;
            if (gi?.Contacts == null) return "no save";
            string want = (contactFilter ?? "").Trim();
            Contact bestC = null, anyC = null;
            TextMessage bestM = null, anyM = null;
            long bestT = long.MinValue, anyT = long.MinValue, pos = 0;
            foreach (var c in gi.Contacts)
            {
                if (c?.messagesQueue == null) continue;
                if (want.Length > 0 && !string.Equals(c.id ?? "", want, StringComparison.Ordinal)) continue;
                OwnedContactAddress(c, out string why, out _);
                if (why != null) continue;                            // not a contact of mine - the send gate's own answer
                foreach (var m in c.messagesQueue)
                {
                    pos++;
                    if (m == null || m.isFromPlayer || IsRelayedCopy(m)) continue;
                    long when = StampOf(m, pos);
                    if ((m.additionalData?.contextButtonData?.Count ?? 0) > 0)
                    { if (when >= bestT) { bestT = when; bestC = c; bestM = m; } }
                    else if (when >= anyT) { anyT = when; anyC = c; anyM = m; }
                }
            }
            if (bestM == null) { bestC = anyC; bestM = anyM; }
            if (bestM == null) return "no native message with buttons on my contacts";
            contactName = bestC.id ?? "";
            messageKey  = bestM.messageKey ?? "";
            buttons     = bestM.additionalData?.contextButtonData?.Count ?? 0;
            string fresh;
            try { fresh = RelayNow(bestC, bestM, false); }
            catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
            sent  = !string.IsNullOrEmpty(fresh);
            newId = sent ? fresh : (FindMineId(bestM) ?? "");
            return "";
        }

        /// <summary>The game's own message clock (TextMessage.timestamp, Day/Hour/Minute), with the queue
        /// position as the tie-break and the whole answer when a message carries no timestamp.</summary>
        private static long StampOf(TextMessage m, long pos)
        {
            try
            {
                var t = m.timestamp;
                if (t != null) return (((long)t.Day * 1440L + (long)t.Hour * 60L + (long)t.Minute) * 100000L) + pos;
            }
            catch { }
            return pos;
        }

        /// <summary>FNV-1a, hex - a short stable id, the notification relay's own shape.</summary>
        private static string Fnv(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char c in s) { h ^= c; h *= 16777619u; }
                return h.ToString("x8");
            }
        }
    }

    // -- patches ---------------------------------------------------------------

    /// <summary>r2 MAJOR-2. THE OWNER'S OWN PRESS. The click the game wires onto a context button is an
    /// anonymous delegate inside ContextButton.SetUp(string, TextMessage.ContextButtonData,
    /// AdditionalMessageData) - decompile UI.Smartphone.Apps.Contacts/ContextButton.cs:72-78:
    ///     if (_button.interactable) { _button.interactable = false; buttonData.onClick();
    ///                                 messageData?.contextButtonData?.Clear();
    ///                                 ContactsApp.SetContextButtonsNoninteractable(groupId); }
    /// It has no name of its own, so the target is the compiler's closure class: the nested type of
    /// ContextButton that carries BOTH captured fields ('buttonData' and 'messageData', the decompile's own
    /// names), and its one void parameterless method. TargetMethods (plural) is used on purpose - a build
    /// where that shape is gone yields NOTHING instead of aborting PatchAll for every other patch.
    /// The postfix reads the captured 'messageData' and hands it to the relay, which tells the company the
    /// message was answered here. Nothing is changed about the local press itself.</summary>
    [HarmonyPatch]
    public static class Patch_ContextButton_NativePress
    {
        private static System.Reflection.FieldInfo _messageDataField;

        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var found = new List<System.Reflection.MethodBase>();
            try
            {
                foreach (var nested in typeof(ContextButton).GetNestedTypes(AccessTools.all))
                {
                    var fmd = nested.GetField("messageData", AccessTools.all);
                    if (fmd == null || fmd.FieldType != typeof(AdditionalMessageData)) continue;
                    if (nested.GetField("buttonData", AccessTools.all) == null) continue;
                    foreach (var mi in nested.GetMethods(AccessTools.all))
                    {
                        if (mi.IsStatic || mi.DeclaringType != nested) continue;
                        if (mi.ReturnType != typeof(void) || mi.GetParameters().Length != 0) continue;
                        _messageDataField = fmd;
                        found.Add(mi);
                    }
                }
            }
            catch (Exception ex)
            { Plugin.Logger.LogWarning($"[Messages] resolving the native press hook: {ex.GetType().Name}: {ex.Message}"); }
            if (found.Count == 0)
                Plugin.Logger.LogWarning("[Messages] this build's ContextButton click handler was not found - a message answered on THIS machine's own phone leaves every partner copy showing a button that cannot run.");
            return found;
        }

        static void Postfix(object __instance)
        {
            try
            {
                if (_messageDataField == null || __instance == null) return;
                CompanyMessages.OnNativePress(_messageDataField.GetValue(__instance));
            }
            catch { }
        }
    }

    /// <summary>THE GATEWAY (decompile Entities/Contact.cs:98-119): every message in the game is enqueued
    /// here, so one postfix sees them all. Postfix, so the local message has already been raised - the
    /// relay never changes what this machine shows.</summary>
    [HarmonyPatch(typeof(Contact), nameof(Contact.SendMessage))]
    public static class Patch_Contact_SendMessage_CompanyRelay
    {
        static void Postfix(Contact __instance, TextMessage textMessage, bool notify)
        {
            try { CompanyMessages.OnLocalMessage(__instance, textMessage, notify); } catch { }
        }
    }
}
