using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Helpers;             // PlayerHelper (held item for Deposit; player position for auto-close)
using Entities;            // InstanceBehavior<GameManager> (de-select the borrowed proxy on close)
using BigAmbitions.Items;  // CargoInstance (item icon via ItemCached)
using Player.HUD.ItemInfoOverlays;  // VehicleOverlay (clone the native Enter/Manage menu)
using HarmonyLib;                   // AccessTools (read the native UI's private serialized fields)
using UI.MergeCargo;                // ManageCargoUi (the native cargo screen we clone)
using UI.PlayerHUD;                 // CargoItemUi (the native card whose fields we set directly)
using Localizor;                    // .Localize (native sell-confirm body — sell parity 2026-08-25)
using Extensions;                   // ToShortCurrencyFormat (sell-confirm price)

namespace BigAmbitionsMP
{
    /// <summary>
    /// A non-owner's view of another player's UNLOCKED vehicle storage, styled to read like the game's
    /// own cargo screen — white item cards (the game's icon + name + amount) on the native frame, a
    /// "Boxes" header, and a green Enter / blue Manage-Storage menu. Behaviour is OURS and safe: Take/Put
    /// route through VehicleStorageSync (host-authoritative request/grant). PASSIVE — reads
    /// VehicleManager.GhostCargoFor(vid) and re-renders on cargo change. Closes when you walk away
    /// (mirrors the native panel). Ticked from MPCanvasUI.Update.
    /// </summary>
    internal static class VehicleStoragePanel
    {
        // Build marker — printed on first build so the deployed DLL version is verifiable from the log.
        private const string Version = "vstore-2026-06-22f-deposit-walk";
        private const int MaxRows = 12;
        private const float WalkAwayDistance = 8f;
        private enum Mode { List, Choice }

        private static readonly Color CardColor   = new Color(0.96f, 0.96f, 0.96f, 0.98f);   // white card
        private static readonly Color DepositCard = new Color(0.80f, 0.93f, 0.80f, 0.98f);   // greenish = deposit
        private static readonly Color TextDark    = new Color(0.10f, 0.11f, 0.13f, 1f);      // dark text on white
        private static readonly Color BtnBlue     = new Color(0.20f, 0.45f, 0.80f, 1f);
        private static readonly Color BtnGreen    = new Color(0.36f, 0.70f, 0.36f, 1f);

        private static GameObject _canvas, _root, _panel, _rowsRoot, _menuClone;
        private static TextMeshProUGUI _title, _boxes;
        private static bool _built;

        private static Mode _mode = Mode.List;
        private static string _vid = "", _owner = "", _sig = "";

        // ── v17 trunk detail (proposal 2, display parity): the owner's FULL cargo answer.
        // _detailRows = the current rows (null → manifest fallback); _detailSig = the manifest
        // signature the rows were accepted under (manifest moved → rows stale → drop + re-ask);
        // _detailPendingSig = the signature a request is in flight for (one ask per state).
        private static System.Collections.Generic.List<CargoDetailInfo>? _detailRows;
        private static string _detailSig = "", _detailPendingSig = "";
        private static System.Action _enterCb;

        public static bool IsOpen => _vid != "";

        public static void OpenChoice(string vid, string ownerId, System.Action enterCb)
        {
            if (string.IsNullOrEmpty(vid)) return;
            if (!_built) Build();
            if (_canvas == null) return;
            EnsureTooltipsAboveUs();
            _vid = vid; _owner = ownerId ?? ""; _enterCb = enterCb; _mode = Mode.Choice;
            _detailRows = null; _detailSig = ""; _detailPendingSig = "";   // fresh session, fresh detail
            if (!CloneMenu())   // exact native menu; on failure, fall back to the hand-built one
            {
                SizePanel(440f, 250f);
                if (_panel != null) _panel.SetActive(true);
                RenderChoice();
            }
            if (_root != null) _root.SetActive(true);
            Plugin.Logger.LogInfo($"[VStore] choice menu OPENED for '{vid}' (owner '{_owner}').");
        }

        // Clone the game's own VehicleOverlay (the Enter Vehicle / Manage Storage menu) for a pixel-exact
        // look, strip its driver script, and wire our actions onto its buttons. Returns false to fall back.
        private static bool CloneMenu()
        {
            try
            {
                if (_menuClone != null) { UnityEngine.Object.Destroy(_menuClone); _menuClone = null; }
                var native = UnityEngine.Object.FindObjectOfType<VehicleOverlay>(true);
                if (native == null || native.transform.parent == null) return false;
                if (_panel != null) _panel.SetActive(false);   // show the clone instead of our frame

                // Clone the COMPLETE container (DetailedItemOverlay): dark frame + title + the entity
                // sub-overlays. It carries the sprites/font/layout + a ContentSizeFitter that re-sizes it.
                var clone = UnityEngine.Object.Instantiate(native.transform.parent.gameObject);
                clone.SetActive(true);

                // Disable the native driver scripts so they don't re-show/hide things or NRE.
                foreach (var comp in clone.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null) continue;
                    var cn = comp.GetType().Name;
                    if ((cn == "DetailedOverlay" || cn == "VehicleOverlay" || cn == "ButtonOverlay"
                         || cn == "StorageShelfOverlay" || cn == "MachineOverlay") && comp is Behaviour beh)
                        beh.enabled = false;
                }
                // The container is a shared AMALGAMATION of EVERY entity sub-overlay (vehicle, cash register,
                // employee, radio, machine, …) — the game shows only the relevant subset per what was clicked.
                // Keep ONLY the frame + title + the vehicle section; hide every other child.
                foreach (Transform child in clone.transform)
                {
                    string n = child.name;
                    bool keep = n == "Background" || n == "HeaderField" || n == "VehicleSplitter" || n == "VehicleDetailedOverlay";
                    child.gameObject.SetActive(keep);
                }
                // Title (plain TMP, no localization component) — give it a generic label.
                foreach (var tmp in clone.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
                    if (tmp.gameObject.name == "HeaderField") { tmp.text = HeaderTitle(); break; }
                // Repurpose the vehicle buttons (Add-Item/Add-cart stay as the game left them — inactive on foot).
                var enterCb = _enterCb;
                foreach (var b in clone.GetComponentsInChildren<Button>(true))
                {
                    string bn = b.gameObject.name;
                    if (bn.Contains("EnterVehicle"))
                    {
                        b.gameObject.SetActive(true);
                        b.onClick = new Button.ButtonClickedEvent();   // drop inherited listeners (ANTIPATTERNS class 6)
                        b.onClick.AddListener(() => { enterCb?.Invoke(); Close(); });
                    }
                    else if (bn.Contains("ManageStorage"))
                    {
                        b.gameObject.SetActive(true);
                        b.onClick = new Button.ButtonClickedEvent();
                        b.onClick.AddListener(SwitchToList);
                    }
                    else if (bn.Contains("AddItemsToStorage") || bn.Contains("AddHandTruckToStorage"))
                        b.gameObject.SetActive(false);   // not relevant to a non-owner; show only Enter + Manage
                }
                clone.transform.SetParent(_root.transform, false);
                var rt = clone.GetComponent<RectTransform>();
                if (rt != null) { rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f); rt.anchoredPosition = Vector2.zero; }
                clone.SetActive(true);
                _menuClone = clone;
                Plugin.Logger.LogInfo("[VStore] menu clone OK.");
                return true;
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] CloneMenu: {ex.Message}"); return false; }
        }

        private static void SwitchToList()
        {
            if (_menuClone != null) { UnityEngine.Object.Destroy(_menuClone); _menuClone = null; }
            _mode = Mode.List; _sig = " ";
            if (!CloneCargo())
            {
                SizePanel(620f, 540f);
                if (_panel != null) _panel.SetActive(true);
                RenderList();
            }
            // Triage gap (2026-08-25): this route showed the storage without any "opened storage"
            // line — the field report's "I opened an empty inventory" was unfalsifiable from logs.
            Plugin.Logger.LogInfo($"[VStore] opened storage for '{_vid}' (owner '{_owner}', via choice menu).");
        }

        // "<owner>'s <Model>" for the menu title — model via the game's own localizer on the vehicle type
        // (matches OverlayHelper.GetOverlayHeaderText: vehicleType.vehicleTypeName.GetLocalization()).
        private static string HeaderTitle()
        {
            string model = null;
            try { var tn = VehicleManager.TypeNameFor(_vid); if (!string.IsNullOrEmpty(tn)) model = Localize(tn); } catch { }
            if (string.IsNullOrEmpty(model)) model = "Vehicle";
            return string.IsNullOrEmpty(_owner) ? model : $"{_owner}'s {model}";
        }

        // ── native cargo-screen clone (the real ManageCargoUi.panel + its cards) ─────
        private static GameObject _cargoClone;
        private static Transform _cargoContent, _cargoTemplate;
        private static TMP_Text _cargoBoxesTmp;
        private static Button _cargoSellAll;

        // Clone the game's OWN cargo screen (ManageCargoUi.panel: frame + CARGO header + Boxes + scroll grid)
        // onto our 3840 canvas, fill cards from the synced ghost cargo (click a card = Take), repurpose the
        // Sell-All button into Deposit, and wire Close (X) to our Close. Returns false → hand-built fallback.
        private static bool CloneCargo()
        {
            try
            {
                if (_cargoClone != null) { UnityEngine.Object.Destroy(_cargoClone); _cargoClone = null; }
                var mc = UnityEngine.Object.FindObjectOfType<ManageCargoUi>(true);
                if (mc == null) return false;
                var panel       = AccessTools.Field(typeof(ManageCargoUi), "panel")?.GetValue(mc) as GameObject;
                var nativeTmpl  = AccessTools.Field(typeof(ManageCargoUi), "itemTemplate")?.GetValue(mc) as Transform;
                var nativeBoxes = AccessTools.Field(typeof(ManageCargoUi), "contentsLabel")?.GetValue(mc) as Component;
                var nativeSell  = AccessTools.Field(typeof(ManageCargoUi), "sellAllButton")?.GetValue(mc) as Button;
                if (panel == null || nativeTmpl == null) return false;
                if (_panel != null) _panel.SetActive(false);

                var nativeRt = panel.transform as RectTransform;
                Vector2 sz = (nativeRt != null && nativeRt.rect.width > 50f) ? nativeRt.rect.size : new Vector2(1580f, 900f);

                var clone = UnityEngine.Object.Instantiate(panel);
                clone.SetActive(true);

                // Map native descendants → their clone counterparts by sibling-index path (no name dependency).
                _cargoTemplate = MapToClone(nativeTmpl, panel.transform, clone.transform);
                _cargoContent  = _cargoTemplate != null ? _cargoTemplate.parent : null;
                if (_cargoContent == null) { UnityEngine.Object.Destroy(clone); return false; }
                var cloneBoxes = nativeBoxes != null ? MapToClone(nativeBoxes.transform, panel.transform, clone.transform) : null;
                if (cloneBoxes != null)
                {
                    _cargoBoxesTmp = cloneBoxes.GetComponentInChildren<TMP_Text>(true);
                    foreach (var c in cloneBoxes.GetComponents<Component>())
                        if (c != null && c.GetType().Name == "TextLocalizationComponent" && c is Behaviour b) b.enabled = false;
                }
                _cargoSellAll = nativeSell != null ? MapToClone(nativeSell.transform, panel.transform, clone.transform)?.GetComponent<Button>() : null;

                // Close (X): native wires it to ManageCargoUi.Close via a prefab listener — replace with ours.
                foreach (var b in clone.GetComponentsInChildren<Button>(true))
                    if (b.gameObject.name.IndexOf("close", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    { b.onClick = new Button.ButtonClickedEvent(); b.onClick.AddListener(Close); b.gameObject.SetActive(true); break; }

                clone.transform.SetParent(_root.transform, false);
                var rt = clone.GetComponent<RectTransform>();
                if (rt != null)   // center at the native size (preserve size; avoid the stretch→point blow-up)
                { rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f); rt.sizeDelta = sz; rt.anchoredPosition = Vector2.zero; }
                _cargoClone = clone;
                PopulateCargo();
                Plugin.Logger.LogInfo("[VStore] cargo clone OK.");
                return true;
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] CloneCargo: {ex.Message}"); _cargoClone = null; return false; }
        }

        // Review MINOR-F: the wrapper contains the file's documented failure class — a
        // compile-time binding resolving to a runtime-absent overload throws at JIT/prepare time
        // in the ENCLOSING frame, and Tick would re-surface it per movement. The body sets _sig
        // first, so a throw cannot re-fire every frame either way.
        private static void PopulateCargo()
        {
            try { PopulateCargoBody(); }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] PopulateCargo: {ex.Message}"); }
        }

        /// <summary>Risky native-label bindings live ONLY in this non-inlined helper (review
        /// MINOR-F, the :492 GetLocalization precedent): if the running build lacks the
        /// GetItemLabel overload, the JIT failure surfaces at THIS call's site — inside the
        /// callers' try/catch — instead of killing whole render frames.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static object? ItemLabelOrNull(string itemName, int amount)
        {
            try { return LocalizationHelper.GetItemLabel(itemName, amount); } catch { return null; }
        }

        private static void PopulateCargoBody()
        {
            if (_cargoClone == null || _cargoContent == null || _cargoTemplate == null) return;
            _sig = Sig();
            // v17 detail freshness (proposal 2): the manifest moved → any held detail is stale —
            // drop it (manifest fallback below) and ask the owner again. Event-driven: requests
            // fire only here (open + manifest movement), never on a timer; a lost response simply
            // leaves the manifest rendering until the next movement or reopen.
            if (_detailRows != null && _detailSig != _sig) _detailRows = null;
            if (_detailRows == null && _detailPendingSig != _sig)
            {
                _detailPendingSig = _sig;
                // The sig rides the ask and is echoed back verbatim (review MINOR-B) — the answer
                // is matched to ITS request, so an answer to an older ask can never pass as fresh.
                try { VehicleStorageSync.RequestTrunkDetail(_vid, _sig); } catch { }
            }
            for (int i = _cargoContent.childCount - 1; i >= 0; i--)   // clear old cards; keep the template (inactive)
            {
                var ch = _cargoContent.GetChild(i);
                if (ch == _cargoTemplate) { ch.gameObject.SetActive(false); continue; }
                UnityEngine.Object.Destroy(ch.gameObject);
            }
            // Row source (proposal 2): the owner's detail when fresh (real paid/price + nested →
            // the native card shapes), else the 4-field broadcast manifest (24-cap, no nested)
            // exactly as before. Detail SPECIAL rows — sealed boxes and unsealed bundles — render
            // as native singletons (the native grouping never merges a nested-bearing instance,
            // CargoItem.cs:42); PLAIN detail rows feed the same grouped loop the manifest feeds.
            var det = _detailRows;
            var rows = new System.Collections.Generic.List<(string item, int amount, bool paid, float price)>();
            int trueCount;
            if (det != null)
            {
                trueCount = det.Count;
                int specials = 0, specialsCapped = 0;
                foreach (var r in det)
                {
                    if (r == null || string.IsNullOrEmpty(r.ItemName)) continue;
                    // Native's own predicate (CargoItemUi :83) — STATE, not type (review MINOR-J):
                    // a sealed-but-EMPTY instance groups like any plain row, "(NxM)" label and all;
                    // only an instance actually CARRYING contents becomes a singleton card.
                    bool special = r.Nested != null && r.Nested.Count > 0;
                    if (!special) { rows.Add((r.ItemName, r.Amount, r.Paid, r.PricePerUnit)); continue; }
                    if (specials >= 50) { specialsCapped++; continue; }
                    specials++;
                    MakeDetailSpecialCard(r, IsSealedName(r.ItemName));
                }
                if (specialsCapped > 0)   // no silent caps
                    Plugin.Logger.LogInfo($"[VStore] panel display cap: {specialsCapped} sealed/bundle row(s) beyond 50 not rendered.");
            }
            else
            {
                var m = VehicleManager.GhostCargoFor(_vid);
                trueCount = m.Count;
                foreach (var r in m) rows.Add((r.item, r.amount, r.paid, r.price));
            }
            int max = VehicleManager.MaxCargoFor(_vid);
            if (_cargoBoxesTmp != null) _cargoBoxesTmp.text = max > 0 ? $"Boxes: {trueCount}/{max}" : $"Boxes: {trueCount}";

            // Merge identical items into one card (mirrors the native CargoItem grouping — which keys
            // on name+amount+paid, so a paid and an unpaid stack of the same item stay separate
            // cards; ruling 36 makes that distinction real). A click takes ONE box carrying the
            // stack's REAL paid/price (F-2026-08-25-D follow-on: paid:true/price:0 defeated the
            // owner-side exact-stack match and would silently launder unpaid stacks paid).
            var order = new System.Collections.Generic.List<string>();
            var grp = new System.Collections.Generic.Dictionary<string, (int count, string item, int amount, bool paid, float price)>();
            foreach (var r in rows)
            {
                string key = $"{r.item}|{r.amount}|{(r.paid ? 1 : 0)}";
                if (grp.TryGetValue(key, out var g)) grp[key] = (g.count + 1, g.item, g.amount, g.paid, g.price);
                else { grp[key] = (1, r.item, r.amount, r.paid, r.price); order.Add(key); }
            }
            int shown = 0;
            foreach (var key in order)
            {
                if (shown >= 50)   // reachable since the uncapped detail (review MINOR-H) — no silent caps
                { Plugin.Logger.LogInfo($"[VStore] panel display cap: {order.Count - shown} grouped row(s) beyond 50 not rendered."); break; }
                shown++;
                var g = grp[key];
                string amountText = (g.count * g.amount == 1) ? "" : $"{g.count}x{g.amount}";
                string it = g.item; int takeAmt = g.amount; bool tPaid = g.paid; float tPrice = g.price;
                string vid = _vid, owner = _owner;
                // Stage C (M5): a SEALED row takes the WHOLE box (contents echoed) — before this,
                // sealed rows rendered but every take answered "Already taken." (owner-side loose
                // loop skips sealed). Sealed-ness derives from the item DEFINITION (F-2026-08-25-F).
                string tCtx = IsSealedName(it) ? "boxtake" : "";
                // Sell parity (user 2026-08-25): the owner's native card shows a SELL button on
                // paid rows and neither button on sealed rows (CargoItemUi :131-144) — mirror that
                // rule. Confirm dialog + price basis are the native ones (GetSellingPrice × count,
                // the same basis the engine credits on Ok — R2); the removal routes to the owner
                // and the money credits THIS player's wallet at verdict time. Unpaid rows get the
                // native DISCARD instead (user-approved same day; wired below).
                System.Action? onSell = null;
                if (tPaid && tCtx != "boxtake")
                {
                    int cnt = g.count;
                    string amtText = amountText;   // per-card capture for the confirm body
                    onSell = () =>
                    {
                        try
                        {
                            float total = 0f;
                            try { total = new CargoInstance(it, takeAmt, tPrice, true).GetSellingPrice() * cnt; } catch { }
                            // Native confirm shape (CargoItemUi :180-186): localized name, "(NxM) "
                            // prefix when the amount label shows. Localize(it) below is OUR
                            // reflection helper (:392) — ".Localize(new {...})" on the key string is
                            // the Localizor extension; the two coexist by call shape.
                            string locName = Localize(it);
                            HudConfirm.Show(default, "itempanelui_hud_confirm_sellitem".Localize(new
                            {
                                type = string.IsNullOrEmpty(amtText) ? locName : "(" + amtText + ") " + locName,
                                price = total.ToShortCurrencyFormat(),
                            }), delegate
                            {
                                System.Action doSell = () =>
                                {
                                    VehicleStorageSync.RequestStackOp(vid, it, takeAmt, paid: true, tPrice, cnt, sell: true);
                                    Plugin.Logger.LogInfo($"[VStore] borrower stack sell {cnt}×({it}×{takeAmt}) on '{vid}' → routed.");
                                };
                                // Native second confirm for special gifts (CargoItemUi :189-196) —
                                // the borrower keeps the owner's friction (review MINOR-6a).
                                bool gift = false;
                                // != null, not ?. — Item is a ScriptableObject and ?. bypasses Unity's
                                // fake-null (review NEW-6; the IsSealedName idiom).
                                try { var itemDef = BigAmbitions.Items.ItemsGetter.GetByName(it); gift = itemDef != null && itemDef.isSpecialGift; } catch { }
                                if (gift) { try { GiftConfirm(doSell); } catch { doSell(); } }   // JIT-contained (hardening)
                                else doSell();
                            });
                        }
                        catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] sell route: {ex.Message}"); }
                    };
                }
                // Discard parity (user-approved 2026-08-25 evening): the owner's native card shows
                // DISCARD on unpaid rows (CargoItemUi :145-153) — no confirm dialog, ONE instance
                // of the group per click (OnDiscardClick removes firstCargoInstance only). Same
                // routed removal, count 1, no credit. Sealed rows still get neither button.
                System.Action? onDiscard = null;
                if (!tPaid && tCtx != "boxtake")
                {
                    onDiscard = () =>
                    {
                        VehicleStorageSync.RequestStackOp(vid, it, takeAmt, paid: false, tPrice, count: 1, sell: false);
                        Plugin.Logger.LogInfo($"[VStore] borrower stack discard 1×({it}×{takeAmt}) on '{vid}' → routed.");
                    };
                }
                MakeNativeCard(it, amountText, () => VehicleStorageSync.RequestTake(vid, owner, it, takeAmt, tPaid, tPrice, tCtx), paid: tPaid, onSell: onSell, onDiscard: onDiscard);
            }
            if (_cargoSellAll != null)   // Deposit = repurpose the native Sell-All button when holding an item
            {
                bool holding = false;
                try { holding = PlayerHelper.ItemInstanceInHands != null; } catch { }
                if (holding)
                {
                    string vid = _vid, owner = _owner;
                    foreach (var c in _cargoSellAll.GetComponentsInChildren<Component>(true))
                        if (c != null && c.GetType().Name == "TextLocalizationComponent" && c is Behaviour b) b.enabled = false;
                    var lbl = _cargoSellAll.GetComponentInChildren<TMP_Text>(true);
                    if (lbl != null) lbl.text = "Deposit";
                    _cargoSellAll.onClick = new Button.ButtonClickedEvent();
                    _cargoSellAll.onClick.AddListener(() => VehicleStorageSync.RequestDeposit(vid, owner));
                    _cargoSellAll.gameObject.SetActive(true);
                }
                else _cargoSellAll.gameObject.SetActive(false);
            }
        }

        /// <summary>v17 (proposal 2): one native-truth SPECIAL card — a sealed box or an unsealed
        /// bundle (filled bag) from the owner's detail. Native shapes (CargoItemUi :83-108):
        /// sealed → the CONTENTS label ("(400) Hot Dog"), no amount, no tooltip, NO buttons;
        /// unsealed bundle → the container's own name + the contents TOOLTIP, amount hidden, and
        /// (user-approved 2026-08-25) the native buttons: paid → SELL at the nested-INCLUSIVE
        /// native price (contents priced in — CargoItemUi's own GetSellingPrice basis), unpaid →
        /// DISCARD (no confirm, one bag per click). Take = the whole instance (ctx boxtake).</summary>
        private static void MakeDetailSpecialCard(CargoDetailInfo r, bool sealedRow)
        {
            string vid = _vid, owner = _owner;
            string it = r.ItemName; int amt = r.Amount; bool p = r.Paid; float pr = r.PricePerUnit;
            System.Collections.Generic.List<string>? tip = null;
            (string name, int amount)? contents = null;
            System.Action? onSell = null, onDiscard = null;
            if (sealedRow)
            {
                if (r.Nested != null && r.Nested.Count > 0) contents = (r.Nested[0].ItemName, r.Nested[0].Amount);
            }
            else
            {
                if (p)
                {
                    onSell = () =>
                    {
                        try
                        {
                            float total = 0f;
                            try
                            {
                                // The native basis (CargoItemUi OnSellClick → GetSellingPrice →
                                // GetWorth, which INCLUDES nested) over the reconstructed bag.
                                var priced = new CargoInstance(it, amt, pr, true);
                                if (r.Nested != null)
                                    foreach (var n in r.Nested)
                                        if (n != null) priced.nestedCargoInstances.Add(new NestedCargoInstance(n.ItemName, n.Amount, n.PricePerUnit, null));
                                total = priced.GetSellingPrice();
                            }
                            catch { }
                            string locName = Localize(it);
                            HudConfirm.Show(default, "itempanelui_hud_confirm_sellitem".Localize(new
                            {
                                type = locName,   // native hides the amount label on bundle rows → bare name
                                price = total.ToShortCurrencyFormat(),
                            }), delegate
                            {
                                System.Action doSell = () =>
                                {
                                    VehicleStorageSync.RequestBundleOp(vid, it, amt, paid: true, pr, sell: true);
                                    Plugin.Logger.LogInfo($"[VStore] borrower bundle sell {it}×{amt} on '{vid}' → routed (nested-inclusive credit at verdict).");
                                };
                                bool gift = false;
                                try { var itemDef = BigAmbitions.Items.ItemsGetter.GetByName(it); gift = itemDef != null && itemDef.isSpecialGift; } catch { }
                                if (gift) { try { GiftConfirm(doSell); } catch { doSell(); } }
                                else doSell();
                            });
                        }
                        catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] bundle sell route: {ex.Message}"); }
                    };
                }
                else
                {
                    onDiscard = () =>
                    {
                        VehicleStorageSync.RequestBundleOp(vid, it, amt, paid: false, pr, sell: false);
                        Plugin.Logger.LogInfo($"[VStore] borrower bundle discard {it}×{amt} on '{vid}' → routed.");
                    };
                }
                // Native aggregates the tooltip by item name (CargoItemUi :94-106).
                var agg = new System.Collections.Generic.Dictionary<string, int>();
                var ordered = new System.Collections.Generic.List<string>();
                if (r.Nested != null)
                    foreach (var n in r.Nested)
                    {
                        if (n == null || string.IsNullOrEmpty(n.ItemName)) continue;
                        if (agg.TryGetValue(n.ItemName, out var v)) agg[n.ItemName] = v + n.Amount;
                        else { agg[n.ItemName] = n.Amount; ordered.Add(n.ItemName); }
                    }
                tip = new System.Collections.Generic.List<string>();
                foreach (var nm in ordered)
                {
                    string line = nm;
                    var lbl = ItemLabelOrNull(nm, agg[nm]);   // JIT-contained binding (MINOR-F)
                    if (lbl != null) { try { line = lbl.ToString(); } catch { } }
                    tip.Add(line);
                }
            }
            MakeNativeCard(it, "", () => VehicleStorageSync.RequestTake(vid, owner, it, amt, p, pr, "boxtake"),
                           paid: p, onSell: onSell, onDiscard: onDiscard, tooltip: tip, contentsLabel: contents);
        }

        /// <summary>The LAST direct native binding of the risky JIT shape, contained (review
        /// carry-forward, user-approved hardening): an absent ConfirmDiscardingSpecialGift
        /// overload surfaces at THIS call's site — callers catch that and act WITHOUT the gift
        /// friction (the method never began, so the dialog callback was never armed — the
        /// fallback cannot double-fire). A runtime throw INSIDE the dialog is swallowed here
        /// with NO fallback: the callback may already be armed, and acting directly could act
        /// twice. Shared cross-file like Localize.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void GiftConfirm(System.Action onConfirm)
        {
            try { UI.ItemPanel.ItemPanelUI.ConfirmDiscardingSpecialGift(onConfirm); }
            catch (System.Exception ex)
            {
                // No fallback (the callback may be armed — acting directly could act twice), but
                // never a SILENT drop (review MINOR-5): name it so it can't read as a user cancel.
                Plugin.Logger.LogWarning($"[VStore] gift confirm threw mid-dialog — the action was NOT taken: {ex.Message}");
            }
        }

        /// <summary>v17 (proposal 2): the owner's trunk detail arrived. Guards, in order: the R5
        /// session guard (only MY open panel, only THIS vehicle); Ok=false keeps the manifest (a
        /// refusal must never render an empty trunk); a manifest that moved since the request
        /// makes the answer stale — drop it, PopulateCargo already re-asked (or will on its next
        /// signature tick).</summary>
        internal static void ApplyDetail(TrunkDetailResPayload res)
        {
            try
            {
                if (res == null || !IsOpenFor(res.VehicleId)) return;
                if (!res.Ok)
                { Plugin.Logger.LogInfo($"[VStore] trunk detail for '{res.VehicleId}' unavailable ({(res.Rows?.Count ?? 0)} rows ignored) — manifest fallback stays."); return; }
                // Exact request matching (review MINOR-B): the echoed Sig must be the one the
                // CURRENT pending ask carried — an answer to any older ask is stale by definition.
                if ((res.Sig ?? "") != _detailPendingSig)
                { Plugin.Logger.LogInfo($"[VStore] trunk detail for '{res.VehicleId}' answers an older ask — dropped; the current ask's answer is on the way."); return; }
                _detailRows = res.Rows ?? new System.Collections.Generic.List<CargoDetailInfo>();
                _detailSig = _detailPendingSig;
                Plugin.Logger.LogInfo($"[VStore] trunk detail applied for '{res.VehicleId}': {_detailRows.Count} instance(s).");
                if (_mode == Mode.List && _cargoClone != null) PopulateCargo();
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] ApplyDetail: {ex.Message}"); }
        }

        // Instantiate a native cargo card from the cloned template and fill it WITHOUT running CargoItemUi
        // (calling SetUp would run owner-side cargo logic). We set its serialized fields directly via reflection.
        /// <summary>Stage C (M5): does this item NAME derive a sealed container? Same derivation
        /// the game's own CargoInstance.IsSealed getter runs (F-2026-08-25-F: sealed-ness is item
        /// DEFINITION data, never instance state — a name is enough).</summary>
        private static bool IsSealedName(string itemName)
        {
            try
            {
                var it = BigAmbitions.Items.ItemsGetter.GetByName(itemName);
                return it != null && it.HasTag(BigAmbitions.Tags.TagRef.Itemtag.issealedcontainer);
            }
            catch { return false; }
        }

        /// <summary>The game's own unpaid-slot tint (CargoItemUi paints unpaid rows with it, decompile
        /// :154-156) — parity finding (a), 2026-08-25: the borrower's panel showed unpaid stacks in
        /// the normal color while the owner's native view marks them. Fallback only if the HUD is
        /// unreadable at call time.</summary>
        private static Color UnpaidTint()
        {
            try
            {
                var ip = InstanceBehavior<UI.UIs>.Instance?.playerHUD?.itemPanelUI;
                if (ip != null) return ip.cargoUnpaidSlotColor;
            }
            catch { }
            return new Color(0.72f, 0.45f, 0.20f, 1f);
        }

        private static void MakeNativeCard(string itemName, string amountText, UnityEngine.Events.UnityAction onTake, bool paid = true, System.Action? onSell = null, System.Action? onDiscard = null,
                                           System.Collections.Generic.List<string>? tooltip = null, (string name, int amount)? contentsLabel = null)
        {
            var card = UnityEngine.Object.Instantiate(_cargoTemplate, _cargoContent);
            var ci = card.GetComponent<CargoItemUi>();
            if (ci != null)
            {
                ci.enabled = false;
                if (!paid)
                {
                    // Native parity: the same backgroundImage + the same color the game uses.
                    var bg = AccessTools.Field(typeof(CargoItemUi), "backgroundImage")?.GetValue(ci) as Image;
                    if (bg != null) bg.color = UnpaidTint();
                }
                // Name: drive the card's OWN TextLocalizationComponent (set its Key, leave it enabled) — the game
                // localizes it through its working pipeline. Do NOT call GetLocalization ourselves (runtime-absent overload).
                var nl = AccessTools.Field(typeof(CargoItemUi), "nameLabel")?.GetValue(ci);
                if (nl != null)
                {
                    SetKey(nl, itemName);   // the container's own name — stands as the fallback
                    // v17 sealed parity: native labels a sealed box by its CONTENTS ("(400) Hot
                    // Dog", CargoItemUi :88-89 SetData(GetItemLabel(...))). Reflection because the
                    // fragile compile-time bindings burned us before (:389 comment); a miss leaves
                    // the SetKey'd container name — readable, just not native-exact.
                    if (contentsLabel != null) TrySetContentsLabel(nl, contentsLabel.Value.name, contentsLabel.Value.amount);
                }
                var al = AccessTools.Field(typeof(CargoItemUi), "amountLabel")?.GetValue(ci) as TMP_Text;
                if (al != null) { bool show = !string.IsNullOrEmpty(amountText); al.gameObject.SetActive(show); al.text = show ? amountText : ""; }
                foreach (var f in new[] { "priceLabel", "discardButton", "sellButton", "actionButton", "bundleItemsTooltip" })
                {
                    if (f == "sellButton" && onSell != null) continue;      // sell parity: this card keeps its native Sell button
                    if (f == "discardButton" && onDiscard != null) continue; // discard parity: unpaid rows keep the native Discard
                    if (f == "bundleItemsTooltip" && tooltip != null) continue; // v17 bundle parity: contents tooltip stays
                    var c = AccessTools.Field(typeof(CargoItemUi), f)?.GetValue(ci) as Component; if (c != null) c.gameObject.SetActive(false);
                }
                if (tooltip != null)
                {
                    // Native bundle tooltip (CargoItemUi :106-107): a LocalizedListTooltip whose
                    // public 'list' holds the pre-localized contents lines. Any failure HIDES the
                    // tooltip (review MINOR-E) — never populated-nor-hidden indeterminate state.
                    Component? tt = null;
                    try
                    {
                        tt = AccessTools.Field(typeof(CargoItemUi), "bundleItemsTooltip")?.GetValue(ci) as Component;
                        if (tt != null)
                        {
                            var lf = AccessTools.Field(tt.GetType(), "list");
                            if (lf != null) { lf.SetValue(tt, tooltip); tt.gameObject.SetActive(true); }
                            else tt.gameObject.SetActive(false);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Logger.LogWarning($"[VStore] bundle tooltip: {ex.Message}");
                        try { if (tt != null) tt.gameObject.SetActive(false); } catch { }
                    }
                }
                if (onSell != null)
                {
                    var sb = AccessTools.Field(typeof(CargoItemUi), "sellButton")?.GetValue(ci) as Button;
                    if (sb != null)
                    {
                        sb.onClick = new Button.ButtonClickedEvent();   // fresh event — kills any template/prefab listeners (same pattern as itemButton)
                        sb.onClick.AddListener(() => onSell());
                        sb.gameObject.SetActive(true);
                    }
                }
                if (onDiscard != null)
                {
                    var db = AccessTools.Field(typeof(CargoItemUi), "discardButton")?.GetValue(ci) as Button;
                    if (db != null)
                    {
                        db.onClick = new Button.ButtonClickedEvent();
                        db.onClick.AddListener(() => onDiscard());
                        db.gameObject.SetActive(true);
                    }
                }
                var ib = AccessTools.Field(typeof(CargoItemUi), "itemButton")?.GetValue(ci) as Button;
                if (ib != null) { ib.onClick = new Button.ButtonClickedEvent(); ib.onClick.AddListener(onTake); }
            }
            card.gameObject.SetActive(true);
        }

        // Resolve a native descendant of `nativeRoot` to the same node in `cloneRoot` (identical hierarchy).
        private static Transform MapToClone(Transform nativeChild, Transform nativeRoot, Transform cloneRoot)
        {
            if (nativeChild == null || nativeRoot == null || cloneRoot == null) return null;
            var idx = new System.Collections.Generic.List<int>();
            var t = nativeChild;
            while (t != null && t != nativeRoot) { idx.Add(t.GetSiblingIndex()); t = t.parent; }
            if (t != nativeRoot) return null;
            var c = cloneRoot;
            for (int i = idx.Count - 1; i >= 0; i--) { int k = idx[i]; if (k < 0 || k >= c.childCount) return null; c = c.GetChild(k); }
            return c;
        }

        // Localize a game key WITHOUT a compile-time GetLocalization binding (that bound to a runtime-ABSENT
        // LocalizorManager.GetLocalization(string,Object) overload → MissingMethodException that broke the clone).
        // Reflection finds whatever GetLocalization the RUNNING build actually has; Friendly fallback on miss.
        internal static string Localize(string key)   // shared: also used by the assign-dropdown rebuild in MPPatches
        {
            if (string.IsNullOrEmpty(key)) return key ?? "";
            try
            {
                var t = AccessTools.TypeByName("Localizor.LocalizorManager");
                if (t != null)
                    foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    {
                        if (m.Name != "GetLocalization" || m.ReturnType != typeof(string)) continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 0 || ps[0].ParameterType != typeof(string)) continue;
                        var args = new object[ps.Length]; args[0] = key; bool ok = true;
                        for (int i = 1; i < ps.Length; i++)
                        {
                            if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                            else if (!ps[i].ParameterType.IsValueType) args[i] = null;
                            else { ok = false; break; }
                        }
                        if (!ok) continue;
                        var res = m.Invoke(null, args) as string;
                        if (!string.IsNullOrEmpty(res) && res != key) return res;
                    }
            }
            catch { }
            return FriendlyModel(key);
        }

        // Miss-path prettifier for BOTH key families this panel localizes (review NEW-1: the sell
        // confirm was its first ITEM-key caller — "ba:itemname_x" used to render "Itemname x").
        private static string FriendlyModel(string key)
        {
            if (string.IsNullOrEmpty(key)) return "Unknown";   // caller-neutral: vehicle AND item callers share this path
            string s = key;
            int colon = s.IndexOf(':'); if (colon >= 0 && colon < s.Length - 1) s = s.Substring(colon + 1);
            s = s.Replace("vehicletype_", "").Replace("itemname_", "").Replace('_', ' ').Trim();
            if (s.Length == 0) return "Unknown";
            return char.ToUpper(s[0]) + (s.Length > 1 ? s.Substring(1) : "");
        }

        /// <summary>v17 sealed-contents label: native nameLabel.SetData(LocalizationHelper.
        /// GetItemLabel(name, amount)) via reflection (SetData's parameter type is Localizor
        /// runtime data we do not bind at compile time). A miss is silent — the SetKey'd
        /// container name already stands.</summary>
        private static void TrySetContentsLabel(object nameLabel, string itemName, int amount)
        {
            try
            {
                object? label = ItemLabelOrNull(itemName, amount);   // JIT-contained binding (MINOR-F)
                if (label == null) return;
                foreach (var m in nameLabel.GetType().GetMethods())
                {
                    if (m.Name != "SetData") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || !ps[0].ParameterType.IsInstanceOfType(label)) continue;
                    m.Invoke(nameLabel, new[] { label });
                    return;
                }
            }
            catch { }
        }

        // Set a TextLocalizationComponent's Key (property or field) via reflection — the game then localizes it
        // through its OWN (working) pipeline, so we never call the fragile GetLocalization extension ourselves.
        private static void SetKey(object localizationComponent, string key)
        {
            try
            {
                var t = localizationComponent.GetType();
                var prop = t.GetProperty("Key");
                if (prop != null && prop.CanWrite) { prop.SetValue(localizationComponent, key); return; }
                AccessTools.Field(t, "Key")?.SetValue(localizationComponent, key);
            }
            catch { }
        }

        public static void Open(string vid, string ownerId)
        {
            if (string.IsNullOrEmpty(vid)) return;
            if (!_built) Build();
            if (_canvas == null) return;
            EnsureTooltipsAboveUs();
            _vid = vid; _owner = ownerId ?? ""; _mode = Mode.List; _sig = " ";
            _detailRows = null; _detailSig = ""; _detailPendingSig = "";
            if (!CloneCargo())   // exact native cargo screen; on failure, fall back to the hand-built list
            {
                SizePanel(620f, 540f);
                if (_panel != null) _panel.SetActive(true);
                RenderList();
            }
            if (_root != null) _root.SetActive(true);
            Plugin.Logger.LogInfo($"[VStore] opened storage for '{vid}' (owner '{_owner}').");
        }

        /// <summary>Field find (user 2026-08-25): the bundle tooltip draws on TooltipSystem's OWN
        /// canvas — our panel at 5001 sat ABOVE it, burying the borrower's tooltip (the owner's
        /// native window sits BELOW the tooltip canvas, so only ours clipped it). Review MINOR-1
        /// killed the first cut (lowering OUR canvas has no safe floor — it could land under
        /// PassengerHud 5000 or, for a low tooltip order, under MPCanvasUI/native HUD). The
        /// correct direction: tooltips are TOPMOST BY DESIGN — RAISE the game's tooltip canvas
        /// just above us. Native relative order is unchanged (it was already above every native
        /// canvas); our 5001/5000 toast adjacency stays intact; and any native tooltip that was
        /// under our chat/HUD canvases is fixed for free. Idempotent (once above, the condition
        /// is false); re-tried on every Open in case the singleton wasn't alive at first build;
        /// the singleton is DontDestroyOnLoad, so the raise persists.</summary>
        private static void EnsureTooltipsAboveUs()
        {
            try
            {
                var cv = _canvas != null ? _canvas.GetComponent<Canvas>() : null;
                if (cv == null) return;
                var ts = InstanceBehavior<Tooltip.TooltipSystem>.Instance;
                var tc = ts != null ? AccessTools.Field(typeof(Tooltip.TooltipSystem), "canvas")?.GetValue(ts) as Canvas : null;
                if (tc != null && tc.sortingOrder <= cv.sortingOrder)
                {
                    int was = tc.sortingOrder;
                    tc.sortingOrder = cv.sortingOrder + 1;
                    Plugin.Logger.LogInfo($"[VStore] tooltip canvas raised {was} → {tc.sortingOrder} (tooltips are topmost by design; our panel stays {cv.sortingOrder}).");
                }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] tooltip-sort probe: {ex.Message}"); }
        }

        /// <summary>Round-35: is the panel currently showing THIS vehicle? Take-result deliveries close the
        /// panel on success ("you carry it now"), but a LATE result from an earlier failed click was closing
        /// a freshly-reopened panel before the user could do anything (probe caller = PlaceForAccessor via
        /// OnResult, right after "opened storage"). Results may only close their own panel session.</summary>
        public static bool IsOpenFor(string vid) => !string.IsNullOrEmpty(vid) && _vid == vid;

        public static void Close()
        {
            string vidWas = _vid;   // capture before clearing — used for the highlight reset below (round-12 #3)
            // Triage gap (2026-08-25): a menu that "went away by itself" was invisible in logs —
            // every close now says what closed. (The mode says whether a CHOICE menu or the LIST died.)
            if (vidWas != "") Plugin.Logger.LogInfo($"[VStore] panel CLOSED for '{vidWas}' (mode {_mode}).");
            _vid = ""; _owner = ""; _enterCb = null;
            _detailRows = null; _detailSig = ""; _detailPendingSig = "";
            if (_menuClone != null) { UnityEngine.Object.Destroy(_menuClone); _menuClone = null; }
            if (_cargoClone != null) { UnityEngine.Object.Destroy(_cargoClone); _cargoClone = null; _cargoContent = null; _cargoTemplate = null; _cargoBoxesTmp = null; _cargoSellAll = null; }
            if (_root != null) _root.SetActive(false);
            // Bug (2026-06-30): clicking a borrowed car to open its trunk SELECTS it (GameManager.selectedVehicle =
            // the proxy). The game then refuses to let you walk into a building "while in a vehicle", and that
            // selection is normally cleared by the native deselect — which our trunk redirect bypasses. We're on
            // FOOT (just looked in the trunk), so de-select the borrowed proxy here. A real DRIVEN vehicle
            // (GetCurrentVehicle != null) is left alone.
            try
            {
                var gm = InstanceBehavior<GameManager>.Instance;
                var sel = gm?.selectedVehicle;
                var cur = VehicleHelper.GetCurrentVehicle();
                // (round-35 insta-close diag retired 2026-07-05: root = a late take result closing a newer
                // panel session, fixed via IsOpenFor scoping; the caller-trace confirmed it.)
                if (sel != null && sel.vehicleInstance?.id != null && sel.vehicleInstance.id.StartsWith("BAMP_") && cur == null)
                { gm.selectedVehicle = null; Plugin.Logger.LogInfo("[VStore] de-selected borrowed proxy on close."); }
            }
            catch { }
            // Round-12 #3: the panel opened from a click on the ghost (hover → outline ON); closing it is the
            // end of OUR flow, and no native hover-exit follows — clear the stuck outline explicitly.
            if (!string.IsNullOrEmpty(vidWas)) VehicleManager.ClearGhostHighlight(vidWas);
        }

        public static void Tick()
        {
            if (_vid == "") return;
            // Owner vanished → close. LOCKED storage stays open to a granted key-holder (mirrors
            // VehicleStorageSync.OwnerApply's lock bypass): without this a shared-but-LOCKED car's panel closed
            // the instant it opened (user 2026-06-30). Diagnostics name the auto-close reason — remove once settled.
            if (string.IsNullOrEmpty(VehicleManager.OwnerIdFor(_vid))) { Plugin.Logger.LogInfo("[VStore] auto-close: owner gone"); Close(); return; }
            if (PassengerSync.IsLocked(_vid) && !GrantSync.IsGranted(_owner, MPConfig.PlayerId)) { Plugin.Logger.LogInfo("[VStore] auto-close: locked + not a key-holder"); Close(); return; }
            if (WalkedAway()) { Plugin.Logger.LogInfo("[VStore] auto-close: walked away"); Close(); return; }   // mirror the native panel closing when you leave
            if (_mode == Mode.List && Sig() != _sig) { if (_cargoClone != null) PopulateCargo(); else RenderList(); }
        }

        private static bool WalkedAway()
        {
            try
            {
                var ghost = VehicleManager.GhostTransform(_vid);
                var ch = PlayerHelper.PlayerController?.Character;
                if (ghost == null || ch == null) return false;
                return Vector3.Distance(ghost.position, ch.transform.position) > WalkAwayDistance;
            }
            catch { return false; }
        }

        private static string Sig()
        {
            // Paid state is part of the signature (ruling 35): the checkout mirror's OpMarkPaid
            // changes ONLY the paid flags, and an open panel must repaint on that echo.
            var rows = VehicleManager.GhostCargoFor(_vid);
            var sb = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
                sb.Append(rows[i].item).Append('=').Append(rows[i].amount).Append('=').Append(rows[i].paid ? 1 : 0).Append(';');
            return sb.ToString();
        }

        private static void ClearRows()
        {
            if (_rowsRoot == null) return;
            for (int i = _rowsRoot.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_rowsRoot.transform.GetChild(i).gameObject);
        }

        private static void RenderChoice()
        {
            ClearRows();
            if (_title != null) _title.text = "Vehicle";
            if (_boxes != null) _boxes.text = "";
            var enterCb = _enterCb;
            MakeWideButton("Enter Vehicle",  BtnGreen, () => { enterCb?.Invoke(); Close(); });
            MakeWideButton("Manage Storage", BtnBlue,  () => { _mode = Mode.List; _sig = " "; SizePanel(620f, 540f); RenderList(); });
        }

        private static void RenderList()
        {
            if (_rowsRoot == null) return;
            _sig = Sig();
            ClearRows();
            var rows = VehicleManager.GhostCargoFor(_vid);
            if (_title != null) _title.text = "Storage";
            if (_boxes != null) _boxes.text = rows.Count > MaxRows ? $"Boxes: {MaxRows}+ of {rows.Count}" : $"Boxes: {rows.Count}";
            MakeDepositRow();
            int shown = 0;
            for (int i = 0; i < rows.Count && shown < MaxRows; i++, shown++)
            {
                // Ruling 36: unpaid stacks are takeable exactly like the owner's own — the native
                // store-exit gate is the only enforcement. Real paid/price ride the request so the
                // owner-side exact-stack match hits the right stack and paid state never launders.
                string item = rows[i].item; int amt = rows[i].amount;
                bool paid = rows[i].paid; float price = rows[i].price;
                string vid = _vid, owner = _owner;
                string ctx = IsSealedName(item) ? "boxtake" : "";   // Stage C (M5): sealed = whole box
                MakeCard(item, amt, paid ? CardColor : UnpaidTint(), "Take", BtnBlue,
                         () => VehicleStorageSync.RequestTake(vid, owner, item, amt, paid, price, ctx));
            }
        }

        private static void MakeDepositRow()
        {
            if (_rowsRoot == null) return;
            CargoInstance ci = null;
            try { var held = PlayerHelper.ItemInstanceInHands; if (held != null) ci = held.ConvertToCargoInstance(); } catch { }
            if (ci == null || string.IsNullOrEmpty(ci.itemName) || ci.amount <= 0) return;
            string item = ci.itemName; int amt = ci.amount; bool paid = ci.paid; float price = ci.pricePerUnit;
            string vid = _vid, owner = _owner;
            MakeCard(item, amt, DepositCard, "Deposit", BtnGreen,
                     () => VehicleStorageSync.RequestDeposit(vid, owner), holdingPrefix: true);
        }

        // One white (or green) card: [icon] name ×amount [action button]. The action closure is passed in.
        private static void MakeCard(string itemName, int amount, Color cardColor, string action, Color actionColor,
                                     UnityEngine.Events.UnityAction onClick, bool holdingPrefix = false)
        {
            if (_rowsRoot == null) return;
            var card = new GameObject("Card");
            card.transform.SetParent(_rowsRoot.transform, false);
            card.AddComponent<RectTransform>();
            var le = card.AddComponent<LayoutElement>();
            le.minHeight = 56f; le.preferredHeight = 56f;
            var bg = card.AddComponent<Image>();
            bg.color = cardColor;

            AddIcon(card.transform, itemName);
            string text = (holdingPrefix ? "Holding: " : "") + $"{Friendly(itemName)}   ×{amount}";
            var lbl = AddLabel(card.transform, text, 22f, TextAlignmentOptions.Left, TextDark);
            var lrt = lbl.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0.12f, 0f); lrt.anchorMax = new Vector2(0.72f, 1f);
            lrt.offsetMin = new Vector2(8f, 0f); lrt.offsetMax = Vector2.zero;

            var btn = MakeButton(card.transform, action, actionColor, onClick);
            var brt = btn.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(0.74f, 0.14f); brt.anchorMax = new Vector2(0.97f, 0.86f);
            brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
        }

        private static void AddIcon(Transform card, string itemName)
        {
            Sprite icon = null;
            try { icon = new CargoInstance(itemName, 1, 0f, true).ItemCached?.icon; } catch { }
            if (icon == null) return;
            var go = new GameObject("Icon");
            go.transform.SetParent(card, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.14f); rt.anchorMax = new Vector2(0.12f, 0.86f);
            rt.offsetMin = new Vector2(8f, 0f); rt.offsetMax = new Vector2(-2f, 0f);
            var img = go.AddComponent<Image>();
            img.sprite = icon; img.preserveAspect = true;
        }

        // ── build ────────────────────────────────────────────────────────────────
        private static void Build()
        {
            _built = true;
            try
            {
                var canvasGO = new GameObject("BAMP_VehicleStorage");
                UnityEngine.Object.DontDestroyOnLoad(canvasGO);
                var canvas = canvasGO.AddComponent<Canvas>();
                canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 5001;   // the tooltip canvas is raised above us at Open (EnsureTooltipsAboveUs)
                var scaler = canvasGO.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(3840f, 2160f);   // MATCH the game's overlay canvas (probe: ItemOverlayManager) — our 1920 ref rendered cloned UI 2x too big
                scaler.matchWidthOrHeight = 0.5f;
                canvasGO.AddComponent<GraphicRaycaster>();
                _canvas = canvasGO;

                _root = new GameObject("Root");
                _root.transform.SetParent(canvasGO.transform, false);
                var rootrt = _root.AddComponent<RectTransform>();
                rootrt.anchorMin = Vector2.zero; rootrt.anchorMax = Vector2.one;
                rootrt.offsetMin = Vector2.zero; rootrt.offsetMax = Vector2.zero;

                var backdrop = new GameObject("Backdrop");
                backdrop.transform.SetParent(_root.transform, false);
                var brt = backdrop.AddComponent<RectTransform>();
                brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
                brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
                var bImg = backdrop.AddComponent<Image>();
                bImg.color = new Color(0f, 0f, 0f, 0.45f);
                var bBtn = backdrop.AddComponent<Button>();
                bBtn.transition = Selectable.Transition.None;
                bBtn.onClick.AddListener(Close);

                _panel = new GameObject("Panel");
                _panel.transform.SetParent(_root.transform, false);
                var prt = _panel.AddComponent<RectTransform>();
                prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
                prt.sizeDelta = new Vector2(620f, 540f);
                prt.anchoredPosition = Vector2.zero;
                var bg = _panel.AddComponent<Image>();
                bg.color = new Color(0.13f, 0.14f, 0.17f, 0.98f);   // dark frame (the cargo clone will replace this hand-built panel next)

                // Header: dark bar with title (left), box count (right), close (far right).
                var header = new GameObject("Header");
                header.transform.SetParent(_panel.transform, false);
                var hrt = header.AddComponent<RectTransform>();
                hrt.anchorMin = new Vector2(0f, 1f); hrt.anchorMax = new Vector2(1f, 1f); hrt.pivot = new Vector2(0.5f, 1f);
                hrt.sizeDelta = new Vector2(0f, 60f); hrt.anchoredPosition = Vector2.zero;
                header.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);
                _title = AddLabel(header.transform, "Storage", 28f, TextAlignmentOptions.Left, Color.white);
                var trt = _title.GetComponent<RectTransform>();
                trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(0.45f, 1f);
                trt.offsetMin = new Vector2(18f, 0f); trt.offsetMax = Vector2.zero;
                _boxes = AddLabel(header.transform, "", 22f, TextAlignmentOptions.Right, new Color(0.85f, 0.85f, 0.85f, 1f));
                var bxrt = _boxes.GetComponent<RectTransform>();
                bxrt.anchorMin = new Vector2(0.45f, 0f); bxrt.anchorMax = new Vector2(0.82f, 1f);
                bxrt.offsetMin = Vector2.zero; bxrt.offsetMax = new Vector2(-8f, 0f);
                var close = MakeButton(header.transform, "X", new Color(0.5f, 0.2f, 0.2f, 1f), Close);
                var crt = close.GetComponent<RectTransform>();
                crt.anchorMin = new Vector2(0.86f, 0.16f); crt.anchorMax = new Vector2(0.98f, 0.84f);
                crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;

                _rowsRoot = new GameObject("Rows");
                _rowsRoot.transform.SetParent(_panel.transform, false);
                var rrt = _rowsRoot.AddComponent<RectTransform>();
                rrt.anchorMin = new Vector2(0f, 0f); rrt.anchorMax = new Vector2(1f, 1f); rrt.pivot = new Vector2(0.5f, 1f);
                rrt.offsetMin = new Vector2(12f, 12f); rrt.offsetMax = new Vector2(-12f, -68f);
                var vlg = _rowsRoot.AddComponent<VerticalLayoutGroup>();
                vlg.childControlWidth = true; vlg.childControlHeight = false;
                vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
                vlg.spacing = 6f; vlg.childAlignment = TextAnchor.UpperCenter;

                _root.SetActive(false);
                Plugin.Logger.LogInfo($"[VStore] panel built — {Version}.");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] panel build: {ex.Message}"); }
        }

        private static void SizePanel(float w, float h)
        {
            if (_panel == null) return;
            var rt = _panel.GetComponent<RectTransform>();
            if (rt != null) rt.sizeDelta = new Vector2(w, h);
        }

        // ── small UI helpers ──
        private static void MakeWideButton(string label, Color color, UnityEngine.Events.UnityAction onClick)
        {
            if (_rowsRoot == null) return;
            var go = new GameObject(label + "Button");
            go.transform.SetParent(_rowsRoot.transform, false);
            go.AddComponent<RectTransform>();
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 70f; le.preferredHeight = 70f;
            var img = go.AddComponent<Image>(); img.color = color;
            var btn = go.AddComponent<Button>(); btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);
            AddLabel(go.transform, label, 26f, TextAlignmentOptions.Center, Color.white);
        }

        private static Button MakeButton(Transform parent, string label, Color color, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(label + "Button");
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>(); img.color = color;
            var btn = go.AddComponent<Button>(); btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);
            AddLabel(go.transform, label, 20f, TextAlignmentOptions.Center, Color.white);
            return btn;
        }

        private static TextMeshProUGUI AddLabel(Transform parent, string text, float size, TextAlignmentOptions align, Color color)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text; tmp.fontSize = size; tmp.color = color; tmp.alignment = align;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6f, 0f); rt.offsetMax = new Vector2(-6f, 0f);
            return tmp;
        }

        private static string Friendly(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return "item";
            string s = itemName;
            int colon = s.IndexOf(':');
            if (colon >= 0 && colon < s.Length - 1) s = s.Substring(colon + 1);
            s = s.Replace("itemname_", "").Replace('_', ' ').Trim();
            if (s.Length == 0) return itemName;
            var sb = new StringBuilder(s.Length);
            bool cap = true;
            foreach (char c in s) { sb.Append(cap ? char.ToUpper(c) : c); cap = c == ' '; }
            return sb.ToString();
        }
    }

    /// <summary>Shared vehicles — DUPLICATION FIX (2026-06-30). A borrowed car is a registered proxy in the
    /// borrower's fleet with a "BAMP_"+realId vehicleInstance.id; the game's native "Manage Storage" opens
    /// ManageCargoUi on that LOCAL copy, so a take (VehicleInstance.RemoveFromCargo) mutates only the copy —
    /// the owner's real cargo is untouched and the next fleet sync re-adds the item → the taken item DUPLICATES.
    /// Redirect the native storage-open for a borrowed proxy into our own panel, whose Take/Put route through
    /// VehicleStorageSync (owner-authoritative). The borrower's own cars (no BAMP_ prefix) keep the native UI.</summary>
    [HarmonyPatch(typeof(VehicleController), "ManageStorage")]
    public static class Patch_VehicleController_ManageStorage_Borrowed
    {
        static bool Prefix(VehicleController __instance)
        {
            try
            {
                var inst = __instance?.vehicleInstance;
                if (inst == null || string.IsNullOrEmpty(inst.id) || !inst.id.StartsWith("BAMP_")) return true;   // my own car → native
                string realId = inst.id.Substring(5);
                string owner = VehicleManager.OwnerIdFor(realId);
                if (string.IsNullOrEmpty(owner)) return true;   // can't resolve the owner → fall back to native
                VehicleStoragePanel.Open(realId, owner);         // owner-authoritative take/put, no local-copy mutation
                return false;
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] ManageStorage redirect: {ex.Message}"); return true; }
        }
    }
}
