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
        private static System.Action _enterCb;

        public static bool IsOpen => _vid != "";

        public static void OpenChoice(string vid, string ownerId, System.Action enterCb)
        {
            if (string.IsNullOrEmpty(vid)) return;
            if (!_built) Build();
            if (_canvas == null) return;
            _vid = vid; _owner = ownerId ?? ""; _enterCb = enterCb; _mode = Mode.Choice;
            if (!CloneMenu())   // exact native menu; on failure, fall back to the hand-built one
            {
                SizePanel(440f, 250f);
                if (_panel != null) _panel.SetActive(true);
                RenderChoice();
            }
            if (_root != null) _root.SetActive(true);
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

        private static void PopulateCargo()
        {
            if (_cargoClone == null || _cargoContent == null || _cargoTemplate == null) return;
            _sig = Sig();
            for (int i = _cargoContent.childCount - 1; i >= 0; i--)   // clear old cards; keep the template (inactive)
            {
                var ch = _cargoContent.GetChild(i);
                if (ch == _cargoTemplate) { ch.gameObject.SetActive(false); continue; }
                UnityEngine.Object.Destroy(ch.gameObject);
            }
            var rows = VehicleManager.GhostCargoFor(_vid);
            int max = VehicleManager.MaxCargoFor(_vid);
            if (_cargoBoxesTmp != null) _cargoBoxesTmp.text = max > 0 ? $"Boxes: {rows.Count}/{max}" : $"Boxes: {rows.Count}";

            // Merge identical items into one card (mirrors the native CargoItem grouping): count the instances,
            // keep the per-instance amount → "<count>x<amount>" (blank when 1×1). A click takes ONE box.
            var order = new System.Collections.Generic.List<string>();
            var grp = new System.Collections.Generic.Dictionary<string, (int count, int amount)>();
            foreach (var r in rows)
            {
                if (grp.TryGetValue(r.item, out var g)) grp[r.item] = (g.count + 1, g.amount);
                else { grp[r.item] = (1, r.amount); order.Add(r.item); }
            }
            int shown = 0;
            foreach (var item in order)
            {
                if (shown >= 50) break; shown++;
                var g = grp[item];
                string amountText = (g.count * g.amount == 1) ? "" : $"{g.count}x{g.amount}";
                int takeAmt = g.amount; string it = item, vid = _vid, owner = _owner;
                MakeNativeCard(it, amountText, () => VehicleStorageSync.RequestTake(vid, owner, it, takeAmt, true, 0f));
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

        // Instantiate a native cargo card from the cloned template and fill it WITHOUT running CargoItemUi
        // (calling SetUp would run owner-side cargo logic). We set its serialized fields directly via reflection.
        private static void MakeNativeCard(string itemName, string amountText, UnityEngine.Events.UnityAction onTake)
        {
            var card = UnityEngine.Object.Instantiate(_cargoTemplate, _cargoContent);
            var ci = card.GetComponent<CargoItemUi>();
            if (ci != null)
            {
                ci.enabled = false;
                // Name: drive the card's OWN TextLocalizationComponent (set its Key, leave it enabled) — the game
                // localizes it through its working pipeline. Do NOT call GetLocalization ourselves (runtime-absent overload).
                var nl = AccessTools.Field(typeof(CargoItemUi), "nameLabel")?.GetValue(ci);
                if (nl != null) SetKey(nl, itemName);
                var al = AccessTools.Field(typeof(CargoItemUi), "amountLabel")?.GetValue(ci) as TMP_Text;
                if (al != null) { bool show = !string.IsNullOrEmpty(amountText); al.gameObject.SetActive(show); al.text = show ? amountText : ""; }
                foreach (var f in new[] { "priceLabel", "discardButton", "sellButton", "actionButton", "bundleItemsTooltip" })
                { var c = AccessTools.Field(typeof(CargoItemUi), f)?.GetValue(ci) as Component; if (c != null) c.gameObject.SetActive(false); }
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
        private static string Localize(string key)
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

        private static string FriendlyModel(string key)
        {
            if (string.IsNullOrEmpty(key)) return "Vehicle";
            string s = key;
            int colon = s.IndexOf(':'); if (colon >= 0 && colon < s.Length - 1) s = s.Substring(colon + 1);
            s = s.Replace("vehicletype_", "").Replace('_', ' ').Trim();
            if (s.Length == 0) return "Vehicle";
            return char.ToUpper(s[0]) + (s.Length > 1 ? s.Substring(1) : "");
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
            _vid = vid; _owner = ownerId ?? ""; _mode = Mode.List; _sig = " ";
            if (!CloneCargo())   // exact native cargo screen; on failure, fall back to the hand-built list
            {
                SizePanel(620f, 540f);
                if (_panel != null) _panel.SetActive(true);
                RenderList();
            }
            if (_root != null) _root.SetActive(true);
            Plugin.Logger.LogInfo($"[VStore] opened storage for '{vid}' (owner '{_owner}').");
        }

        /// <summary>Round-35: is the panel currently showing THIS vehicle? Take-result deliveries close the
        /// panel on success ("you carry it now"), but a LATE result from an earlier failed click was closing
        /// a freshly-reopened panel before the user could do anything (probe caller = PlaceForAccessor via
        /// OnResult, right after "opened storage"). Results may only close their own panel session.</summary>
        public static bool IsOpenFor(string vid) => !string.IsNullOrEmpty(vid) && _vid == vid;

        public static void Close()
        {
            string vidWas = _vid;   // capture before clearing — used for the highlight reset below (round-12 #3)
            _vid = ""; _owner = ""; _enterCb = null;
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
                // DIAG:INVESTIGATION(cart-panel-insta-close) — the flatbed menu/panel closed right after
                // opening with NO auto-close reason logged (2026-07-04) → an untraced Close() caller. Name it.
                string caller = "";
                try
                {
                    var st = new System.Diagnostics.StackTrace(1, false);
                    for (int f = 0; f < Math.Min(3, st.FrameCount); f++)
                        caller += (f > 0 ? "<" : "") + (st.GetFrame(f)?.GetMethod()?.DeclaringType?.Name ?? "?") + "." + (st.GetFrame(f)?.GetMethod()?.Name ?? "?");
                }
                catch { }
                Plugin.Logger.LogInfo($"[VStore] close: selVeh='{(sel?.vehicleInstance?.id ?? "null")}' curVeh='{(cur?.id ?? "null")}' caller={caller}.");   // diag: remove once re-entry + insta-close settled
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
            var rows = VehicleManager.GhostCargoFor(_vid);
            var sb = new StringBuilder();
            for (int i = 0; i < rows.Count; i++) sb.Append(rows[i].item).Append('=').Append(rows[i].amount).Append(';');
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
                string item = rows[i].item; int amt = rows[i].amount; string vid = _vid, owner = _owner;
                MakeCard(item, amt, CardColor, "Take", BtnBlue,
                         () => VehicleStorageSync.RequestTake(vid, owner, item, amt, true, 0f));
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
                canvas.sortingOrder = 5001;
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
