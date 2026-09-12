using System;
using System.Collections.Generic;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Merger slice 6 - BUSINESS-SCOPED NOTIFICATION RELAY (company-merger-map section 26; user
    /// requirement 2026-09-10: the top-right pop-ups must MATCH on every member of a merged company).
    ///
    /// A merged company is ONE business, so when a partner shop reports a sick employee, an arrived
    /// delivery or an unpaid licence, every member must see the same pop-up - but the game only
    /// raises it on the machine that owns the shop.
    ///
    /// NO NEW ON-SCREEN TEXT: the relay carries the GAME OWN headerKey + notificationData, and the
    /// receiver calls the game own Notifications.Show with them, so the wording, icon and
    /// localisation are the game. Merger EVENT pop-ups (formed / joined / left) are NOT here.
    ///
    /// Flow: postfix on the Notifications.Show GATEWAY (never the UI renderer) -> allow-list gate ->
    /// "is this MY business" gate -> host -> every OTHER ONLINE member of THE SENDER GROUP -> the
    /// receiver shows it untracked (nothing is persisted) and click-inert (v1).
    ///
    /// INERT without a merger: the postfix returns at MergerSync.IAmMember; single-player is doubly
    /// inert (no session, no group).
    /// </summary>
    public static class NotificationRelay
    {
        /// <summary>THE ALLOW-LIST - the business-scoped header keys, taken verbatim from the game
        /// own callers (decompile line numbers beside each; F-2026-09-10-E). The game has 449 Show
        /// sites in 177 files: everything NOT listed here stays local (personal money, energy,
        /// tutorial, vehicle, housing, ...). Keys whose data names no business at all (the "multiple"
        /// roll-ups, which carry only a count) sit here harmlessly: the owner gate below drops them.
        /// </summary>
        private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // Helpers/EmployeeHelper.cs
            "employeehelper_notification_employee_amount_called_in_sick",         // :198  count only
            "employeehelper_notification_employee_called_in_sick",                // :207  businessName
            "employeehelper_notification_employee_retired",                       // :225  businessName
            "employeehelper_notification_employee_amount_finished_training",      // :296  count only
            "employeehelper_notification_employee_finished_training_female",      // :305  name = person
            "employeehelper_notification_employee_finished_training_male",        // :305  name = person
            "employeehelper_notification_emloyee_resigned",                       // :338  businessName (game typo)
            "employeehelper_notification_emloyee_resigned_multiple",              // :348  count only
            // Helpers/BusinessHelper.cs
            "notification_delivery_contract_arrived",                             // :349  fromname/toname
            "businesshelper_notification_no_employee_assigned",                   // :492  name
            "businesshelper_notification_no_employee_assigned_two",               // :492  fromname/toname
            "businesshelper_notification_no_employee_assigned_more",              // :492  businessName
            // Buildings.Retail.Businesses.CinemaTheater/LicensingFeesHelper.cs
            "notification_unpaid_licensing_fee_multiple",                         // :123  count only
            "notification_unpaid_licensing_fee",                                  // :129  businessName
            // Helpers/MarketingHelper.cs
            "marketing_notification_no_money_campaigns_disabled",                 // :57   businessName
            "marketing_notification_no_money_campaigns_disabled_multiple",        // :67   count only
            // FoodDelivery/FoodDeliveryHelper.cs :239, FurnitureStore/FurnitureDeliveryHelper.cs :193
            "notification_delivery_contract_arrived_business_name",               // businessName
            // Buildings.Office.Headquarters/HeadhunterPlan.cs
            "notifications_headhunter_employee_replaced",                         // :344  person names only
            "notifications_headhunters_employee_replaced_multiple",               // :354  count only
            "notifications_headhunter_employee_replaced_multiple",                // :370  count only
            // Buildings.Office.Headquarters/LogisticsManagerPlan.cs
            "delivery_alert_notification",                                        // :245  name
            "delivery_alert_notification_multiple",                               // :245  count only
            // Helpers/ScheduleAutoFillerHelper.cs
            "bizman_schedule_auto_fill_notify",                                   // :94   businessName
            "bizman_schedule_auto_fill_notify_partial",                           // :94   businessName
            // (bizman_schedule_auto_fill_notify_cancel is deliberately NOT here: ScheduleAutoFillerHelper.cs:58 is
            //  autonomous, but BizManSchedule.cs:166 raises the SAME key from a button - it would toast a partner
            //  for a click the member made.)
            // Helpers/PrivateDriverHelpers.cs
            "ba:private_driver_notification_unpaid",                              // :538  fee only
            // Entities/ContactsHelper.cs
            "employeehelper_notification_employee_amount_messaged_you",           // :96   count only
            // ── MERGER PHASE 4d (R5): five more AUTONOMOUS company events ─────────────────────────────
            // Buildings.Office.Headquarters/PricingManagerPlan.cs
            "notifications_PricingManager_mispriced_items",                       // :195  employeeName + amount - resolved by the named employee's workplace (D24); an ambiguous name drops
            // Helpers/RealEstateHelper.cs - the daily "a competitor bought your building" pass
            "real_estate_building_sold_notification",                             // :132  address/price/amount
            "real_estate_building_sold_notification_items_sold",                  // :132  + itemsAmount
            // Entities/ImportPartnership.cs - the daily import deliveries (NotifyDeliveries)
            "import_partnership_notification_shipment_arrived",                   // :436  name = the business
            "import_partnership_notification_shipment_multiple",                  // :431  count only — never resolves (no name, no address): sits here per the roll-up convention, drops at the owner gate (4d review MINOR-4)
        };

        /// <summary>4d R5: the keys 4d DROPPED from the list above - personal mail and personal parking tickets are
        /// not company events (design read section E; D22). Kept as a named record so the removal is auditable.</summary>
        private static readonly string[] Dropped4d = { "contacts_new_message", "contacts_parking_tickets_received" };

        private static readonly string[] Added4d =
        {
            "notifications_PricingManager_mispriced_items", "real_estate_building_sold_notification",
            "real_estate_building_sold_notification_items_sold", "import_partnership_notification_shipment_arrived",
            "import_partnership_notification_shipment_multiple",
        };

        private static bool _deltaLogged;

        /// <summary>One INFO pair, the first time a member's machine runs the gate - what 4d added and removed.</summary>
        private static void LogDeltaOnce()
        {
            if (_deltaLogged) return;
            _deltaLogged = true;
            Plugin.Logger.LogInfo($"[Relay] key added {string.Join(", ", Added4d)}");
            Plugin.Logger.LogInfo($"[Relay] key dropped {string.Join(", ", Dropped4d)}");
        }

        /// <summary>The data fields the game uses for a business DISPLAY NAME, in the order they are
        /// tried. businessName comes first on purpose: the sick / retired pop-ups also carry "name",
        /// which is the PERSON, and the delivery pop-ups carry a formatted ADDRESS in toname.</summary>
        private static readonly string[] NameFields = { "businessName", "fromname", "toname", "name" };

        /// <summary>Review r1 (#3/#6): a relayed toast is group-gated but still network input — bound it
        /// before the host fans it out and again before the receiver shows it. A payload the game's own
        /// callers could never produce (an undefined type, a huge data table) is dropped with a WARN.</summary>
        internal static bool PayloadSane(NotificationRelayPayload p, string where)
        {
            string why = null;
            if (p == null) why = "null payload";
            else if (string.IsNullOrEmpty(p.HeaderKey) || p.HeaderKey.Length > 128) why = "header key length";
            else if (!Enum.IsDefined(typeof(UI.Notification.NotificationType), p.Type)) why = $"type {p.Type} undefined";
            else if (p.Data != null)
            {
                if (p.Data.Count > 16) why = $"{p.Data.Count} data entries";
                else foreach (var kv in p.Data)
                    if ((kv.Key?.Length ?? 0) > 64 || (kv.Value?.Length ?? 0) > 512) { why = "data entry length"; break; }
            }
            if (why == null) return true;
            Plugin.Logger.LogWarning($"[NotifyRelay] {where}: dropped '{p?.HeaderKey}' from '{p?.PlayerId}' - {why}.");
            return false;
        }

        /// <summary>Set while THIS machine is showing a RELAYED pop-up, so the sender postfix does not
        /// relay a relay (a two-member loop). Thread-static: Show runs on the main thread, and a
        /// stray background Show must never see another thread flag.</summary>
        [ThreadStatic] private static bool _applying;

        /// <summary>One INFO line per header key that could not be tied to a business of mine - a
        /// roll-up pop-up ("4 employees called in sick") names no business by design, and would
        /// otherwise log every game day.</summary>
        private static readonly HashSet<string> _loggedUnresolved = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>D24: one line per (key, name) when a pricing-manager notice names a person this machine
        /// employs TWICE - the workplace is not guessable, so the notice drops.</summary>
        private static readonly HashSet<string> _loggedAmbiguous = new HashSet<string>(StringComparer.Ordinal);

        // C1 SENDER ---------------------------------------------------------------------------

        /// <summary>THE GATEWAY, not the UI: every caller (including ShowError / ShowInsufficientMoney)
        /// funnels through this one static, and NotificationsUI is only the renderer. Postfix, so the
        /// local pop-up has already been raised natively - the relay never changes what this machine
        /// shows.</summary>
        [HarmonyPatch(typeof(UI.Notification.Notifications), nameof(UI.Notification.Notifications.Show),
            typeof(UI.Notification.NotificationType), typeof(string), typeof(Dictionary<string, string>),
            typeof(float), typeof(string), typeof(Action), typeof(bool), typeof(bool))]
        private static class ShowPatch
        {
            [HarmonyPostfix]
            private static void Postfix(UI.Notification.NotificationType notificationType, string headerKey,
                                        Dictionary<string, string> notificationData, float secondsToShow,
                                        bool notificationSound)
            {
                try
                {
                    if (!MergerSync.IAmMember) return;                                    // C4: inert without a merger
                    LogDeltaOnce();                                                       // 4d R5: name the list change once
                    if (string.IsNullOrEmpty(headerKey) || !Allowed.Contains(headerKey)) return;
                    if (_applying) return;                                                // C3 own Show - never re-relay
                    Relay(notificationType, headerKey, notificationData, secondsToShow, notificationSound);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[NotifyRelay] postfix: {ex.Message}"); }
            }
        }

        /// <summary>Resolve the business the pop-up is about and hand the payload to the host. ONLY MY
        /// businesses relay: a partner pop-up about a partner shop is raised on the partner machine and
        /// relays from there, so gating on MergerFlip.TrulyMine (true ownership, not the presentation
        /// flip) is what stops the same event travelling twice.</summary>
        private static void Relay(UI.Notification.NotificationType type, string headerKey,
                                  Dictionary<string, string> data, float seconds, bool sound)
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return;

            string name = FirstName(data);
            string addressKey = "";
            if (!string.IsNullOrEmpty(name))
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    string bn; try { bn = reg.BusinessName; } catch { continue; }
                    if (!string.Equals(bn, name, StringComparison.Ordinal)) continue;
                    if (!MergerFlip.TrulyMine(reg)) continue;
                    addressKey = GameStateReader.AddressKey(reg);
                    break;
                }

            // MERGER PHASE 4d (R5): some autonomous COMPANY events name no business at all - the real-estate
            // "a competitor bought your building" pair carries a formatted ADDRESS instead (decompile
            // Helpers/RealEstateHelper.cs:132 - address/price/amount/itemsAmount). Without this resolve those keys
            // would sit in the allow-list and never travel. The building is mine if I rent it (the same TrulyMine
            // test as above, so a company building still relays only from its real owner) or if it is in MY
            // real-estate list, which the mod never fills with a partner's property (MPPatches.cs:175).
            if (string.IsNullOrEmpty(addressKey) && data != null
                && data.TryGetValue("address", out var formatted) && !string.IsNullOrEmpty(formatted))
            {
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    string fs; try { fs = Streets.AddressHelper.ToFormattedString(reg.Address); } catch { continue; }
                    if (!string.Equals(fs, formatted, StringComparison.Ordinal)) continue;
                    string k; try { k = GameStateReader.AddressKey(reg); } catch { break; }
                    bool mine = MergerFlip.TrulyMine(reg);
                    if (!mine && !MergerFlip.IsFlipped(k))
                        try { mine = gi.realEstate != null && gi.realEstate.Exists(x => x != null && GameStateReader.AddressKey(x.address) == k); }
                        catch { }
                    if (mine) addressKey = k;
                    break;
                }
            }
            // MERGER PHASE 4d (D24, user 2026-09-11): the pricing-manager notice names NO business and NO address -
            // only the ANALYST (decompile Buildings.Office.Headquarters/PricingManagerPlan.cs:195 - employeeName +
            // amount, where amount is a COUNT of hand-priced items above the neighbourhood ceiling). The company the
            // notice belongs to is the one that named employee WORKS at, so the third resolve walks this machine's
            // roster and takes the single match's workplace. A partner's INJECTED copy is skipped: the notice about a
            // partner's analyst is raised on the partner's machine and relays from there. The workplace registration is
            // then held to the same MergerFlip.TrulyMine test as the name step, so a company building still relays only
            // from its real owner. TWO people with that name is not guessable - it DROPS (one log line, never a guess);
            // ZERO falls through to the ordinary unresolved log. Every read is wrapped so a throwing getter skips that
            // employee, never the relay.
            if (string.IsNullOrEmpty(addressKey) && headerKey == "notifications_PricingManager_mispriced_items"
                && data != null && data.TryGetValue("employeeName", out var who) && !string.IsNullOrEmpty(who))
            {
                int matches = 0;
                string workKey = "";
                bool twoPlaces = false;      // D24: only DIFFERING addresses are an ambiguity
                try
                {
                    var roster = Helpers.EmployeeHelper.GetEmployeeInstances();
                    if (roster != null)
                        foreach (var e in roster)
                        {
                            if (e == null) continue;
                            string eid; try { eid = e.id; } catch { continue; }
                            try { if (MPRegisterSync.IsInjectedStaff(eid)) continue; } catch { continue; }
                            string ename; try { ename = e.characterData?.name?.ToString(); } catch { continue; }
                            if (!string.Equals(ename, who, StringComparison.Ordinal)) continue;
                            string ekey; try { if (e.assignedAddress == null) continue; ekey = GameStateReader.AddressKey(e.assignedAddress); } catch { continue; }
                            if (string.IsNullOrEmpty(ekey)) continue;
                            matches++;
                            if (workKey.Length > 0 && !string.Equals(workKey, ekey, StringComparison.Ordinal)) twoPlaces = true;
                            workKey = ekey;
                        }
                }
                catch { }
                // D24 (r2 MINOR-12): two people of the same name at the SAME workplace name ONE workplace -
                // which is all this resolver needs. It is ambiguous only when the addresses actually differ.
                if (twoPlaces) { LogAmbiguous(headerKey, who, matches); return; }
                if (matches >= 1)
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        string rk; try { rk = GameStateReader.AddressKey(reg); } catch { continue; }
                        if (!string.Equals(rk, workKey, StringComparison.Ordinal)) continue;
                        if (MergerFlip.TrulyMine(reg)) addressKey = rk;
                        break;
                    }
            }

            if (string.IsNullOrEmpty(addressKey)) { LogUnresolved(headerKey, string.IsNullOrEmpty(name) ? "(no business named)" : name); return; }

            var (day, hourOfDay) = GameStateReader.GetGameTime();
            var p = new NotificationRelayPayload
            {
                PlayerId    = MPConfig.PlayerId,
                Type        = (int)type,
                HeaderKey   = headerKey,
                Data        = data == null ? new Dictionary<string, string>() : new Dictionary<string, string>(data),
                AddressKey  = addressKey,
                Seconds     = seconds,
                Sound       = sound,
                StampMinute = (long)day * 1440L + (long)(hourOfDay * 60f),
            };

            // The host is its own relay: call it directly rather than posting to itself (MergerEmployeeSync.Send).
            if (MPServer.IsRunning) MPServer.HostRelayNotification(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected)
                MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.NotificationRelay, MPConfig.PlayerId, p));
        }

        private static string FirstName(Dictionary<string, string> data)
        {
            if (data == null) return "";
            foreach (var f in NameFields)
                if (data.TryGetValue(f, out var v) && !string.IsNullOrEmpty(v)) return v;
            return "";
        }

        private static void LogUnresolved(string headerKey, string name)
        {
            if (!_loggedUnresolved.Add(headerKey)) return;
            Plugin.Logger.LogInfo($"[NotifyRelay] '{headerKey}' not relayed - no owned business named '{name}'");
        }

        /// <summary>D24: the employee->workplace resolve found the name TWICE on this machine. Never guess a company -
        /// drop the notice and say so once per (key, name).</summary>
        private static void LogAmbiguous(string headerKey, string name, int count)
        {
            if (!_loggedAmbiguous.Add(headerKey + "|" + name)) return;
            Plugin.Logger.LogInfo($"[NotifyRelay] '{headerKey}' not relayed - {count} employees named '{name}' (ambiguous)");
        }

        // C3 RECEIVER -------------------------------------------------------------------------

        /// <summary>MAIN THREAD: raise the partner pop-up through the game own gateway, with the same
        /// key and data the sender game used - the mod contributes no text. trackOnSaveGame: false, so
        /// nothing is written into this save notification list; onClickAction: null, because the
        /// game click actions are live closures over the sender objects and cannot travel (inert v1).
        /// The duplicate identifier is derived from key + business + game minute, which is exactly what
        /// the game native de-dupe needs to collapse the same event if it arrives twice.</summary>
        public static void ApplyRelayed(NotificationRelayPayload p)
        {
            try
            {
                if (!PayloadSane(p, "receiver")) return;
                string dup = "bamp-relay-" + Fnv(p.HeaderKey + "|" + p.AddressKey + "|" + p.StampMinute);
                RememberSender(dup, p.PlayerId);   // 4d R5: the renderer tints this toast in the sender's colour
                float seconds = p.Seconds > 0f ? p.Seconds : 4f;
                _applying = true;
                try
                {
                    UI.Notification.Notifications.Show((UI.Notification.NotificationType)p.Type, p.HeaderKey,
                        p.Data, seconds, dup, null, p.Sound, false);
                }
                finally { _applying = false; }
                Plugin.Logger.LogInfo($"[NotifyRelay] shown '{p.HeaderKey}' for '{p.AddressKey}' from '{p.PlayerId}'");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[NotifyRelay] ApplyRelayed: {ex.Message}"); }
        }

        // C5 ATTRIBUTION (MERGER PHASE 4d, R5) -------------------------------------------------
        //
        // D18 says COLOUR, never words: a relayed toast must read as a partner's event without the mod adding one
        // character of text. The gateway cannot do that (it hands the game a key and a data table), so the one new
        // surface is the RENDERER: NotificationsUI.PlayNotification (decompile UI.Notification/NotificationsUI.cs:95)
        // receives the instantiated toast and the duplicateIdentifier, and names the toast GameObject after that id
        // (:103). Every relayed toast already carries the mod's own "bamp-relay-…" id (ApplyRelayed), so the patch
        // recognises its own copies by that id alone and tints the toast LABEL in the sending member's colour. A
        // native toast has a different id (or none) and is never touched.

        private static readonly Dictionary<string, string> _relaySender = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Queue<string> _relaySenderOrder = new Queue<string>();

        private static void RememberSender(string dup, string pid)
        {
            try
            {
                if (string.IsNullOrEmpty(dup) || string.IsNullOrEmpty(pid)) return;
                if (!_relaySender.ContainsKey(dup)) _relaySenderOrder.Enqueue(dup);
                _relaySender[dup] = pid;
                while (_relaySenderOrder.Count > 64) _relaySender.Remove(_relaySenderOrder.Dequeue());
            }
            catch { }
        }

        [HarmonyPatch(typeof(UI.Notification.NotificationsUI), "PlayNotification")]
        private static class TintPatch
        {
            [HarmonyPostfix]
            private static void Postfix(UnityEngine.CanvasGroup entry, string duplicateIdentifier)
            {
                try
                {
                    if (entry == null || string.IsNullOrEmpty(duplicateIdentifier)) return;
                    if (!duplicateIdentifier.StartsWith("bamp-relay-", StringComparison.Ordinal)) return;
                    if (!_relaySender.TryGetValue(duplicateIdentifier, out var pid)) return;
                    if (!PlayerColours.TryColourFor(pid, out var c)) return;
                    // The header label is the child the game itself writes into (:62, the "Text" language-change
                    // component); its own "Text/Timestamp" child keeps the game's colour. 4d r2 (review MINOR-6): no
                    // search wider than that child - the label is the TMP on "Text" itself, else the TextContainer
                    // of "Text"'s own localisation component (the same route the residence row takes); a miss
                    // leaves the toast plain rather than risk tinting the timestamp.
                    var tr = entry.transform.Find("Text");
                    var tmp = tr != null ? tr.GetComponent<TMPro.TMP_Text>() : null;
                    if (tmp == null && tr != null)
                        tmp = HousingMapCues.GetMember(tr.GetComponent<Localizor.LanguageChangeEvent.TextLocalizationComponent>(), "TextContainer") as TMPro.TMP_Text;
                    if (tmp != null) tmp.color = c;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[NotifyRelay] toast tint: {ex.Message}"); }
            }
        }

        /// <summary>FNV-1a, hex - a short stable id for the duplicate identifier (which the game uses
        /// as a GameObject name, so it must be compact and free of surprises).</summary>
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
}
