using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;   // Image, Button, Toggle - the filter row's crosshair graphic
using City.CityMap;   // CityMapFilter, CityMapFilterCategory, CityMapFilterData
using Entities;       // InstanceBehavior
using UI;             // UIs
using UI.Guiders;     // DirectionGuiderType, GuidersManager (the indoors-hide rule)

namespace BigAmbitionsMP
{
    /// <summary>PER-OWNER COLOURS, pieces two and three (2026-09-05/06): a "Players" SECTION of the city-map filter
    /// panel — the game's own header line, with its collapse arrow and its select-all box — sitting ABOVE "Status",
    /// and under it one row per OTHER player, each in that player's slot colour.
    ///
    /// THE SECTION is a real CityMapFilterCategory, created in a POSTFIX of CityMapFilters.InitializeFilters — that
    /// method starts by wiping every clone of its two templates (ClearClones, :188-191), so anything added earlier
    /// or once-only would be thrown away. Each ROW is a real, REGISTERED CityMapFilter (CreateFilter, :372-382), so
    /// the game persists its tick in the save's SelectedCitymapFilters exactly as it does for its own rows, restores
    /// it when the map opens (LoadFilters :203-213) and includes it in the panel's toggle-all (:663-680). A saved
    /// tick for a player who is not here is inert: LoadFilters silently skips a name that has no toggle this session
    /// (:207), and an offline row hides itself through isAvailable.
    ///
    /// A PIN is a plain CityMap.AddPoi on a mod-owned anchor transform. OUTDOORS the anchor follows the remote
    /// player's avatar; INDOORS it sits on that building's first entrance door — the same trick the game plays
    /// with the local player's own pin (PlayerController.SetEntranceDoorAsPoiTarget :366-385), and the reason an
    /// interior's detached coordinates can never leak onto the map. Colour is the ONLY identity channel (plan
    /// ruling), so the pin carries no label text at all.
    ///
    /// NOTHING HERE MAY THROW INTO THE GAME: every entry point is wrapped and logs "[PlayerPins] ...".
    /// MAIN THREAD ONLY: Tick() is driven from MPCanvasUI.Update, right after PlayerColours.Tick().</summary>
    public static class PlayerPins
    {
        private const string Tag = "[PlayerPins]";

        /// <summary>The one and only new on-screen string (user-approved 2026-09-05; since 2026-09-06 it titles the
        /// SECTION instead of a filter row). It is written as RAW TEXT through label.SetValue(clearKey: true), so the
        /// localiser never looks it up; the section's registry key is "bamp_players", which nobody sees.</summary>
        internal const string FilterName = "Players";

        /// <summary>The key CreateLine gave the Status category (CityMapFilters.CreateStatusCategory :217). Our own
        /// section takes its sibling index, so "Players" lands directly above "Status".</summary>
        private const string StatusCategoryKey = "bizman_status";

        /// <summary>The Status category's first NATIVE filter. Fallback anchor for our rows' sibling index, used
        /// only if the header lines and the filter rows turn out not to share a parent (the prefab is not readable).</summary>
        private const string StatusFirstFilter = "buildingresume_rented_by_you";

        // ── The section ───────────────────────────────────────────────────────

        /// <summary>Postfix body: create our own "Players" section and put it above Status.</summary>
        internal static void InstallFilter(CityMapFilters filters)
        {
            try
            {
                if (!(MPServer.IsRunning || MPClient.IsClientInWorld)) { _playersCategory = null; _rows.Clear(); return; }   // r3: no section outside a multiplayer session (F-2026-09-06-X MINOR-4); the predicate is true on a joining client from the transport connect (MPClient.cs:196-199)
                if (filters == null) return;

                // CreateLine names each header clone after its localisation key (:353-355) and GetCategory
                // (:167-170) is the game's own name lookup over _filterCategories — no reflection needed.
                CityMapFilterCategory category = filters.GetCategory(StatusCategoryKey);
                if (category == null)
                {
                    Plugin.Logger.LogWarning($"{Tag} the '{StatusCategoryKey}' category was not found — no '{FilterName}' filter this session.");
                    return;
                }

                CityMapFilterCategory cat = filters.CreateLine("bamp_players");   // our own section (private helper, publicized): header + collapse + select-all, registered with the panel
                if (cat == null) { Plugin.Logger.LogWarning($"{Tag} CreateLine returned nothing — no '{FilterName}' section this session."); return; }
                try { cat.label.SetValue(FilterName, true); } catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} section title: {ex.Message}"); }
                try { cat.transform.SetSiblingIndex(category.transform.GetSiblingIndex()); } catch { }   // ABOVE Status (same parent: both are lineEntry clones)
                _playersCategory = cat;
                _rows.Clear(); _trackerPoi = null; Tracked = null; _placeLogged = false;   // a new CityMapFilters means a new panel: new rows, and the placement case is logged again
                Plugin.Logger.LogInfo($"{Tag} '{FilterName}' section created above Status.");
            }
            catch (Exception ex)
            {
                try { Plugin.Logger.LogWarning($"{Tag} install filter: {ex.Message}"); } catch { }
            }
        }

        // ── Piece three: one filter row per online player ─────────────────────

        /// <summary>One row per online player, created with the game's OWN CreateFilter (CityMapFilters.cs:372-382),
        /// so the row is REGISTERED: it lands in _filterEntries/_filterToggles, the game writes its tick to the save's
        /// SelectedCitymapFilters on every click (CityMapFilter.cs:107/:110), re-ticks it from that list when the map
        /// opens (LoadFilters :203-213) and flips it with the panel's toggle-all (:663-680) — exactly like a native
        /// row. The row's CHECKBOX shows/hides that player's pin (read straight off the toggle); the row's CROSSHAIR
        /// toggles the guide arrow to them. CreateFilter returns the row already AddFilter-ed and activated by the
        /// category's UpdateFilterVisibility, so the raw player NAME is written over the label immediately after —
        /// one Localizor pass may log an unknown-key warning first (accepted: it is gated on non-critical warnings,
        /// LocalizorManager.cs:293-295).</summary>
        private static void EnsureRow(string pid)
        {
            if (_rows.ContainsKey(pid) || _playersCategory == null) return;
            CityMapFilter? row = null; string name = "bamp_player_" + pid;
            try
            {
                var filters = InstanceBehavior<UIs>.Instance.mapFilters;
                string p = pid;
                int idx = RowIndexFor();                                       // BEFORE CreateFilter: excludes this row; throws first if our header is gone
                row = filters.CreateFilter(name, filters.playerIcon, new CityMapFilterData { isAvailable = () => _online.Contains(p) }, _playersCategory);   // REGISTERED: the game persists its tick in the save like any native row
                if (row == null) { _rows[pid] = new PlayerRow { Pid = pid, Name = name }; Plugin.Logger.LogWarning($"{Tag} CreateFilter returned nothing for '{pid}'."); return; }
                var pr = new PlayerRow { Pid = pid, Row = row, Name = name };
                _rows[pid] = pr;                                               // registered the moment it exists (never destroy a registered row — G8)
                try { string nm = MPNames.Resolve(pid); row.label.SetValue(nm, true); pr.Label = nm; } catch { }
                PlaceRow(row, idx);
                WireCrosshair(pr, p);                                          // crosshair: SetActive(true) + onClick -> OnCrosshairClick(p); CrossDefault captured
                SeedFirstSight(pr);                                            // first sight on this machine in this world = ticked
                RefreshRowLook(pr);
                Plugin.Logger.LogInfo($"{Tag} filter row created for '{pid}'.");
            }
            catch (Exception ex)
            {
                if (!_rows.ContainsKey(pid))
                {
                    // r3 (F-2026-09-06-X MINOR-3): `row` is still null whenever CreateFilter itself threw, so ask the
                    // game's OWN registry instead (publicized private Dictionary<string, CityMapFilter>, CityMapFilters.cs:83).
                    // Nothing is ever destroyed: a throw after _filterEntries.Add leaves a registered object the game holds,
                    // and destroying it would break the game's dictionaries.
                    CityMapFilter? live = null;
                    try { InstanceBehavior<UIs>.Instance.mapFilters._filterEntries.TryGetValue(name, out live); } catch { }
                    if (live != null) { _rows[pid] = new PlayerRow { Pid = pid, Row = live, Name = name }; }   // registered and alive: keep it and own it
                    else { _rows[pid] = new PlayerRow { Pid = pid, Name = name }; }                            // nothing registered: never retried this scene
                }
                try { Plugin.Logger.LogWarning($"{Tag} row for '{pid}': {ex.Message}"); } catch { }
            }
        }

        /// <summary>Where this row belongs: our section's header, then the rows already under it. Called BEFORE
        /// CreateFilter, so _rows.Count still excludes this row (row order unchanged) and a destroyed header throws
        /// HERE — before anything of ours has been registered with the panel.</summary>
        private static int RowIndexFor() => _playersCategory!.transform.GetSiblingIndex() + 1 + _rows.Count;

        /// <summary>Headers are lineEntry clones under lineEntry.transform.parent (CityMapFilters.cs:353) and rows are
        /// filterEntry clones under filterEntry.parent (:374); whether those two parents are the same object is not
        /// readable from the prefab. SAME parent → visual order is sibling order, so the row takes the index
        /// RowIndexFor computed. DIFFERENT parent → take the place of the first native Status filter, which is where
        /// our section now sits. Neither → leave the row where the game appended it. Logged once per scene.</summary>
        private static void PlaceRow(CityMapFilter row, int idx)
        {
            try
            {
                Transform rt = row.transform;
                Transform ht = _playersCategory!.transform;
                if (rt.parent != null && ht.parent != null && rt.parent == ht.parent)
                {
                    rt.SetSiblingIndex(Mathf.Min(idx, rt.parent.childCount - 1));
                    if (!_placeLogged) { _placeLogged = true; Plugin.Logger.LogInfo($"{Tag} rows sit under the '{FilterName}' header (headers and rows share a parent; first row at index {rt.GetSiblingIndex()})."); }
                    return;
                }
                Transform? firstNative = (rt.parent != null) ? rt.parent.Find(StatusFirstFilter) : null;
                if (firstNative != null)
                {
                    rt.SetSiblingIndex(firstNative.GetSiblingIndex());
                    if (!_placeLogged) { _placeLogged = true; Plugin.Logger.LogInfo($"{Tag} rows placed at the first native Status filter's index — headers and filter rows do not share a parent."); }
                    return;
                }
                if (!_placeLogged) { _placeLogged = true; Plugin.Logger.LogWarning($"{Tag} could not place the player rows — they stay where the game appended them."); }
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"{Tag} place row: {ex.Message}"); } catch { } }
        }

        /// <summary>The crosshair is OUR tracker toggle. CreateFilter passes no focus point, so SetUp hides
        /// focusButton and wires no click of its own (CityMapFilter.cs:67-76; the else branch at :75) — show it and
        /// add our listener. CrossDefault is captured before anything tints it.</summary>
        private static void WireCrosshair(PlayerRow pr, string p)
        {
            try
            {
                CityMapFilter? row = pr.Row;
                if (row == null) return;
                var btn = row.focusButton;                                     // publicized private Button (CityMapFilter.cs:31)
                if (btn != null)
                {
                    btn.gameObject.SetActive(true);
                    btn.onClick.AddListener(() => OnCrosshairClick(p));
                    pr.Cross = btn.image ?? btn.GetComponentInChildren<Image>(true);
                    if (pr.Cross != null) pr.CrossDefault = pr.Cross.color;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} crosshair for '{p}': {ex.Message}"); }
        }

        /// <summary>User decision 2026-09-06: a player's row starts TICKED the first time this machine sees them in this world, and from
        /// then on the save's own SelectedCitymapFilters rules (the game adds/removes the name on click and re-ticks listed names on
        /// map open). The once-only marker is per (world, player) in the mod's settings store. A saved tick for a player who is not
        /// in the game is inert: LoadFilters skips names without a toggle (CityMapFilters.cs:207) and offline rows are hidden.</summary>
        private static void SeedFirstSight(PlayerRow pr)
        {
            try
            {
                if (pr.Row == null) return;
                var gi = SaveGameManager.Current; if (gi == null || gi.SelectedCitymapFilters == null) return;
                bool listed = gi.SelectedCitymapFilters.Contains(pr.Name);
                // r3 (F-2026-09-06-X MINOR-2): the once-only SEED needs the playthrough id (its marker key is per
                // world); the RESTORE below reads only the save's own list, so it runs whether or not the id is known.
                string id = MPSaveManager.ActivePlaythrough; if (string.IsNullOrEmpty(id)) id = MPSaveCoordinator.ActivePlaythroughId;
                if (!string.IsNullOrEmpty(id))
                {
                    string key = "bamp.player-row-seen." + id + "." + pr.Pid;
                    bool seen = UnityEngine.PlayerPrefs.GetInt(key, 0) != 0;
                    if (!seen)
                    {
                        if (!listed) gi.SelectedCitymapFilters.Add(pr.Name);  // ticked in the save from now on, until the player unticks (the game removes it)
                        UnityEngine.PlayerPrefs.SetInt(key, 1); UnityEngine.PlayerPrefs.Save();
                        listed = true;
                    }
                }
                // rows created after the map's LoadFilters pass (a player who joined mid-session) must reflect the saved tick now
                if (listed && !pr.Row.Toggle.isOn) pr.Row.Toggle.SetIsOnWithoutNotify(true);   // silent: OnToggleClick would reset the player's click (CityMapFilter.cs:104)
                if (pr.Row.Toggle is ToggleExtender te)
                {
                    // r3 (MINOR-1): the silent tick never fires ToggleExtender.OnValueChanged, so mirror ALL of that look, not just the icon (ToggleExtender.cs:22-31)
                    bool on = pr.Row.Toggle.isOn;
                    if (te.icon != null) te.icon.color = on ? te.iconOnColor : Color.white;
                    if (te.background != null) { var sp = on ? te.backgroundOnSprite : te.backgroundOffSprite; if (sp != null) te.background.sprite = sp; }
                }
                try { _playersCategory?.UpdateToggleAllState(); } catch { }   // the silent tick skips the header refresh
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"{Tag} first-sight seed: {ex.Message}"); } catch { } }
        }

        /// <summary>Name + colours on the row: the name via SetValue(clearKey) so Localizor never sees a fake key; the player's colour on the
        /// label text (icon tint is reset by ToggleExtender prefabs) and on the crosshair while this player is tracked.</summary>
        private static void RefreshRowLook(PlayerRow pr)
        {
            if (pr.Row == null) return;
            string name = MPNames.Resolve(pr.Pid);
            if (name != pr.Label) { try { pr.Row.label.SetValue(name, true); pr.Label = name; } catch { } }
            if (PlayerColours.TryColourFor(pr.Pid, out var c) && (!pr.HasColour || !SameColour(c, pr.Colour)))
            { try { pr.Row.SetLabelColor(c); pr.Colour = c; pr.HasColour = true; } catch { } }
            bool lit = Tracked == pr.Pid;
            if (pr.Cross != null && (lit != pr.CrossLit || (lit && pr.HasColour && pr.Cross.color != (Color)pr.Colour)))
            { try { pr.Cross.color = lit && pr.HasColour ? (Color)pr.Colour : pr.CrossDefault; pr.CrossLit = lit; } catch { } }
        }

        /// <summary>The row's crosshair: toggle the guide arrow to this player. On -> lit in the player's colour, the map centres on them once;
        /// off -> normal look, the map stays. One tracked player at a time (another crosshair switches).</summary>
        private static void OnCrosshairClick(string pid)
        {
            try
            {
                if (Tracked == pid) { StopTracking("crosshair"); return; }
                string? prev = Tracked;
                Tracked = pid;
                DestroyTrackerPoi();                                          // a fresh POI for the new target; TickTracker recreates it
                if (prev != null && _rows.TryGetValue(prev, out var ppr)) RefreshRowLook(ppr);
                if (_rows.TryGetValue(pid, out var pr)) RefreshRowLook(pr);
                _centreOnce = true;   // the map centres from TickTracker next frame, after TickOne has refreshed the anchor
                Plugin.Logger.LogInfo($"{Tag} tracking '{pid}'.");
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"{Tag} crosshair: {ex.Message}"); } catch { } }
        }

        private static void StopTracking(string why)
        {
            if (Tracked == null && _trackerPoi == null) return;
            string? pid = Tracked; Tracked = null; _centreOnce = false; DestroyTrackerPoi();
            if (pid != null && _rows.TryGetValue(pid, out var pr)) RefreshRowLook(pr);
            if (pid != null) Plugin.Logger.LogInfo($"{Tag} tracking of '{pid}' stopped ({why}).");
        }

        private static void DestroyTrackerPoi()
        {
            var poi = _trackerPoi; _trackerPoi = null; _trackerLabel = ""; _trackerHasColour = false;
            if (poi == null) return;
            try
            {
                var cm = InstanceBehavior<CityManager>.Instance;
                var map = (cm != null) ? cm.cityMap : null;
                if (map != null && map.pois != null) map.pois.Remove(poi);
            }
            catch { }
            try { UnityEngine.Object.Destroy(poi.gameObject); } catch { }   // PointOfInterest.OnDestroy rebuilds both POI caches (PointOfInterest.cs:248-255)
        }

        // ── The pins ──────────────────────────────────────────────────────────

        private sealed class Pin
        {
            public string Pid = "";
            public GameObject? Anchor;
            public PointOfInterest? Poi;
            public Color32 LastColour;
            public bool HasColour;
            /// <summary>Building key the cached door belongs to ("" = outdoors, or not resolved yet).</summary>
            public string Bldg = "";
            public Transform? Door;
        }

        private static readonly Dictionary<string, Pin> _pins = new Dictionary<string, Pin>();
        private static readonly HashSet<string> _live = new HashSet<string>();
        private static readonly List<string> _gone = new List<string>();

        /// <summary>r2: ResetAll() can be reached from the NETWORK POLL THREAD (PlayerColours.ResetSession,
        /// called by MPClient.OnDisconnected). Unity's SetHidden/Destroy THROW off the main thread and the
        /// throw is swallowed, which used to empty the dictionary while the pins themselves stayed alive.
        /// ResetAll() therefore only raises this flag; Tick() does the destruction on the main thread.</summary>
        private static volatile bool _resetRequested;

        /// <summary>r2: set once every pin has been hidden for a closed map / unticked filter, so the hide
        /// costs one pass and nothing at all on the frames after it.</summary>
        private static bool _allHidden;

        // ── Piece three: per-player rows + the tracker ────────────────────────
        /// <summary>Display PlayerId of the ONE remote player being tracked (guide arrow), or null. Main thread only.</summary>
        internal static string? Tracked { get; private set; }
        private static PointOfInterest? _trackerPoi;              // guider-flagged POI on the tracked player's anchor
        private static string _trackerLabel = "";
        private static bool _centreOnce;                          // r1b: a crosshair switched ON centres the map ONCE, from TickTracker (the click-time anchor is stale)
        private static Color32 _trackerColour;                    // last colour pushed to the tracker POI ...
        private static bool _trackerHasColour;                    // ... so it repaints only on change, exactly as a pin does (:280)
        private sealed class PlayerRow
        {
            public string Pid = ""; public CityMapFilter? Row; public string Label = "";
            public string Name = "";   // "bamp_player_" + pid (the registered filter name)
            public Color32 Colour; public bool HasColour;
            public Image? Cross; public Color CrossDefault = Color.white; public bool CrossLit;   // the crosshair graphic + its untouched colour
        }
        private static readonly Dictionary<string, PlayerRow> _rows = new Dictionary<string, PlayerRow>();   // one filter row per player seen this scene
        private static CityMapFilterCategory? _playersCategory;    // OUR "Players" section: header + collapse arrow + select-all box
        private static bool _placeLogged;                           // the row-placement case is logged once per scene
        private static readonly HashSet<string> _online = new HashSet<string>();   // live remote ids, refreshed by Tick (read by the rows' isAvailable)
        private static bool _rowsDirty;                            // set when _online changes -> refresh row visibility once
        private static bool PinOnFor(string pid) => !_rows.TryGetValue(pid, out var r) || r.Row == null || r.Row.Toggle.isOn;

        /// <summary>MAIN THREAD, once per frame from MPCanvasUI.Update.</summary>
        internal static void Tick()
        {
            try
            {
                // r2: the ONLY place pins are destroyed on a reset. ResetAll() may be raised off-thread.
                if (_resetRequested) { _resetRequested = false; ResetAllNow(); }

                bool inSession = MPServer.IsRunning || MPClient.IsClientInWorld;

                CityManager? cm = null;
                try { cm = InstanceBehavior<CityManager>.Instance; } catch { }
                CityMap? map = (cm != null) ? cm.cityMap : null;

                if (!inSession || map == null)
                {
                    if (_pins.Count > 0 || _online.Count > 0) ResetAllNow();   // one-shot: ResetAllNow empties both (rows stay, hidden via the empty _online)
                    return;
                }

                bool show = CityMap.IsOpen;   // pins are gated per row by the row's own toggle; the section's select-all flips them all

                // ── who is online this frame: rows and pins both read this ────
                // r1b (review F-2026-09-06-P MINOR-5): GetRemotePlayerIds builds a fresh List over the registry's
                // Keys on EVERY call (RemotePlayerManager.cs:394-395), and nothing this block feeds can be SEEN
                // unless the map PANEL is open (the rows) or somebody is tracked (the arrow). Off both, skip the
                // roster read entirely and fall straight through to the one-time hide pass below. _live is then
                // only ever READ on frames this block has just refilled: every later path needs show (which
                // implies CityMap.IsOpen) or Tracked != null.
                if (CityMap.IsOpen || Tracked != null)
                {
                    _live.Clear();
                    var ids = RemotePlayerManager.GetRemotePlayerIds();
                    if (ids != null)
                    {
                        for (int i = 0; i < ids.Count; i++)
                        {
                            string pid = ids[i];
                            if (string.IsNullOrEmpty(pid) || pid == MPConfig.PlayerId) continue;   // never a pin for yourself
                            _live.Add(pid);
                        }
                    }

                    // ── piece three: one filter row per online player ─────────
                    bool changed = _online.Count != _live.Count || !_online.SetEquals(_live);
                    if (changed) { _online.Clear(); foreach (var id in _live) _online.Add(id); _rowsDirty = true; }
                    foreach (var id in _live) EnsureRow(id);
                    foreach (var kv in _rows) RefreshRowLook(kv.Value);            // name/colour/crosshair state (cheap compares)
                    if (_rowsDirty && _playersCategory != null) { _rowsDirty = false; try { _playersCategory.UpdateFilterVisibility(); } catch { } }   // re-applies isAvailable (CityMapFilterCategory.cs:164-176; publicized private)
                    if (Tracked != null && !_online.Contains(Tracked)) StopTracking("not connected");
                }

                // r2: with the map closed nothing can be seen, so do no PIN work at
                // all — no pin creation, no position maths. Every existing pin is hidden once and _allHidden keeps
                // every later frame free. A player who LEAVES while hidden therefore keeps a (hidden) pin until the
                // next frame with show == true, which removes it before anything shows.
                // PIECE THREE: a TRACKED player keeps this pass alive with the map SHUT — that is what feeds the
                // edge arrow and its live distance (PermanentPointsOfInterest runs before CityMap's !IsOpen return).
                if (!show && Tracked == null)
                {
                    if (!_allHidden)
                    {
                        foreach (var kv in _pins)
                        {
                            var poi = kv.Value.Poi;
                            try { if (poi != null) poi.SetHidden(true); } catch { }
                        }
                        _allHidden = true;
                    }
                    return;
                }
                _allHidden = false;

                // departed players first, so a pin never outlives its player by a frame
                if (_pins.Count > _live.Count)
                {
                    _gone.Clear();
                    foreach (var kv in _pins) if (!_live.Contains(kv.Key)) _gone.Add(kv.Key);
                    for (int i = 0; i < _gone.Count; i++) RemovePin(_gone[i], map);
                    _gone.Clear();
                }

                Sprite? icon = PlayerIcon();
                foreach (var pid in _live)
                {
                    bool isTracked = pid == Tracked;
                    if (!show && !isTracked)
                    {
                        // nothing visible for this one: the map is shut and someone ELSE is tracked
                        try { if (_pins.TryGetValue(pid, out var hp) && hp.Poi != null && !hp.Poi.hidden) hp.Poi.SetHidden(true); } catch { }
                        continue;
                    }
                    try { TickOne(pid, map, icon, show, isTracked); }
                    catch (Exception ex) { Warn("pin '" + pid + "'", ex); }
                }
            }
            catch (Exception ex) { Warn("tick", ex); }
        }

        private static void TickOne(string pid, CityMap map, Sprite? icon, bool show, bool isTracked)
        {
            Pin p = EnsurePin(pid, map, icon);
            if (p.Poi == null || p.Anchor == null) return;

            // ── where the pin sits this frame ────────────────────────────────
            bool placed = false;
            string bldg = RemotePlayerManager.BuildingOf(pid) ?? "";
            if (bldg.Length > 0)
            {
                Transform? door = DoorOf(p, bldg, out bool followAvatar);
                if (door != null) { p.Anchor.transform.position = door.position; placed = true; }
                // r2: a Hamptons house has no entrance door of its own — the game points the pin at the BODY
                // there, so follow the remote avatar instead of leaving the pin hidden.
                else if (followAvatar && RemotePlayerManager.TryGetRemotePosition(pid, out var hpos))
                { p.Anchor.transform.position = hpos; placed = true; }
            }
            else
            {
                p.Bldg = "";
                p.Door = null;
                // Outdoors a remote avatar is NEVER hidden by the cross-interior mask (RemotePlayerManager
                // SpawnOrUpdate, round-87), so its transform is the live, smoothed position — the stale-while-
                // masked case only arises indoors, and indoors we use the door instead.
                if (RemotePlayerManager.TryGetRemotePosition(pid, out var pos)) { p.Anchor.transform.position = pos; placed = true; }
            }

            // ── the colour on the round blob (repainted only when it changes) ─
            if (PlayerColours.TryColourFor(pid, out var c) && (!p.HasColour || !SameColour(c, p.LastColour)))
            {
                p.Poi.SetIcon(icon, c);
                p.LastColour = c;
                p.HasColour = true;
            }

            // the row's checkbox gates this player's pin; the TRACKED player's pin yields to the tracker POI,
            // which sits on the SAME anchor and draws the arrow + distance itself
            p.Poi.SetHidden(!(show && placed && PinOnFor(pid)) || isTracked);
            if (isTracked) TickTracker(p, placed);
        }

        /// <summary>The guide arrow: a SECOND POI on the tracked player's anchor, flagged as a guider so the game
        /// draws it with the map CLOSED. PointOfInterest.UpdatePosition renders the off-screen edge arrow (:142-154)
        /// and the live distance after the label (:156-165); the pump is PermanentPointsOfInterest.HandlePermanentPOIs,
        /// called from CityMap.LateUpdate BEFORE its !IsOpen return. SetGuider must run before the POI's first real
        /// enable (PointOfInterest.Start :82-110 disables a NON-guider's container for good) and the type must not be
        /// Destination or PrivateDriver (both wire destructive buttons) - MainQuest wires nothing.
        /// The indoors rule is the game's own, applied HERE: it only ever runs it over its five prefab guiders.</summary>
        private static void TickTracker(Pin pin, bool placed)
        {
            try
            {
                if (_trackerPoi == null)
                {
                    CityManager? cm = null;
                    try { cm = InstanceBehavior<CityManager>.Instance; } catch { }
                    CityMap? map = (cm != null) ? cm.cityMap : null;
                    if (map == null || pin.Anchor == null) return;
                    Color32 c = pin.HasColour ? pin.LastColour : HousingMapCues.SharedColor;
                    string label = MPNames.Resolve(pin.Pid);
                    var poi = map.AddPoi(pin.Anchor.transform, PlayerIcon(), c, label, null);   // the same call shape the pins use (:308)
                    if (poi == null) return;
                    poi.SetGuider(DirectionGuiderType.MainQuest);   // BEFORE its first enable (PointOfInterest.cs:82-110); MainQuest wires nothing destructive
                    _trackerPoi = poi; _trackerLabel = label; _trackerColour = c; _trackerHasColour = true;
                    Plugin.Logger.LogInfo($"{Tag} tracker arrow created for '{pin.Pid}'.");
                }
                if (pin.HasColour && (!_trackerHasColour || !SameColour(pin.LastColour, _trackerColour)))
                { _trackerPoi.SetIcon(PlayerIcon(), pin.LastColour); _trackerColour = pin.LastColour; _trackerHasColour = true; }
                string want = MPNames.Resolve(pin.Pid); if (want != _trackerLabel) { _trackerPoi.SetText(want); _trackerLabel = want; }
                if (_centreOnce && placed) { _centreOnce = false; try { var v = pin.Anchor!.transform.position; if (v != Vector3.zero) InstanceBehavior<CityManager>.Instance.cityMap.cityMapCam.MoveCameraToTarget(v); } catch { } }
                bool hidden = !placed || GuidersManager.ShouldGuidersBeHidden();   // the game applies this only to its five native guiders (GuidersManager.cs:165-171)
                _trackerPoi.SetHidden(hidden);
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"{Tag} tracker: {ex.Message}"); } catch { } }
        }

        private static Pin EnsurePin(string pid, CityMap map, Sprite? icon)
        {
            if (!_pins.TryGetValue(pid, out var p)) { p = new Pin { Pid = pid }; _pins[pid] = p; }

            // A destroyed anchor with a surviving pin would leave the pin chasing a dead transform: tear the pair
            // down and rebuild it rather than half-repair it.
            if (p.Anchor == null && p.Poi != null) DestroyPinObjects(p, map);

            if (p.Anchor == null)
            {
                p.Anchor = new GameObject("BAMP_PlayerPin_" + pid);
                p.Bldg = "";
                p.Door = null;
            }

            if (p.Poi == null)
            {
                Color32 c = PlayerColours.TryColourFor(pid, out var col) ? col : HousingMapCues.SharedColor;
                p.Poi = map.AddPoi(p.Anchor.transform, icon, c, "", null);   // "" = no label; colour is the only identity channel
                if (p.Poi != null)
                {
                    p.Poi.SetHidden(true);
                    p.LastColour = c;
                    p.HasColour = true;
                }
                // The map moves non-permanent pins from a CACHED array that is rebuilt only on request
                // (CityMap.LateUpdate :99-103); without this the new pin would never be positioned.
                try { map.UpdateNonePermanentPointOfInterests(); } catch { }
            }

            return p;
        }

        /// <summary>The building's first entrance door, cached per pin until that player changes building. The
        /// Hamptons house has no entrance door of its own: the game targets the PLAYER BODY there
        /// (PlayerController.SetEntranceDoorAsPoiTarget :366-384 — cityBuildingController.building.IsHamptonsHouse()
        /// at :375-377 → _poi.target = base.transform). r2 reports that through followAvatar so the caller
        /// positions the anchor on the remote avatar instead of hiding the pin; only when there is neither a
        /// door nor an avatar position does the pin stay hidden for that frame.</summary>
        private static Transform? DoorOf(Pin p, string bldg, out bool followAvatar)
        {
            followAvatar = false;
            if (p.Bldg == bldg && p.Door != null) return p.Door;

            var cbc = FindBuilding(bldg);
            if (cbc != null && cbc.building != null && cbc.building.IsHamptonsHouse())
            {
                p.Bldg = "";   // there is no door to cache here
                p.Door = null;
                followAvatar = true;
                return null;
            }
            var doors = (cbc != null) ? cbc.entranceDoors : null;   // BuildingEntranceDoor[] — an ARRAY, so .Length
            Transform? t = null;
            if (doors != null && doors.Length > 0 && doors[0] != null) t = doors[0].doorTransform;
            if (t != null) { p.Bldg = bldg; p.Door = t; }
            return t;
        }

        private static void RemovePin(string pid, CityMap? map)
        {
            if (_pins.TryGetValue(pid, out var p)) { DestroyPinObjects(p, map); _pins.Remove(pid); }
        }

        /// <summary>CityMap has NO RemovePoi: `pois` is a public List (CityMap.cs:38) and AddPoi (:426-438) is the
        /// only half of the pair the game wrote. Order matters — a destroyed pin left in `pois`, or in the cached
        /// non-permanent array LateUpdate walks, throws out of the map every frame.</summary>
        private static void DestroyPinObjects(Pin p, CityMap? map)
        {
            var poi = p.Poi;
            p.Poi = null;
            p.HasColour = false;
            // ReferenceEquals, not ==: an ALREADY-destroyed pin reads null through Unity's operator but is still
            // sitting in the map's list, and that is exactly the entry that has to come out.
            if (!ReferenceEquals(poi, null))
            {
                try { if (map != null && map.pois != null) map.pois.Remove(poi); } catch { }
                try { if (poi != null) { poi.SetHidden(true); UnityEngine.Object.Destroy(poi.gameObject); } } catch { }
                try { if (map != null) map.UpdateNonePermanentPointOfInterests(); } catch { }
            }

            var anchor = p.Anchor;
            p.Anchor = null;
            p.Bldg = "";
            p.Door = null;
            try { if (anchor != null) UnityEngine.Object.Destroy(anchor); } catch { }
        }

        /// <summary>Every pin goes: called when the session ends (PlayerColours.ResetSession) and whenever the
        /// world or the session is not there any more. ANY THREAD — ResetSession runs on the network poll
        /// thread (MPClient.OnDisconnected), where Unity's SetHidden/Destroy throw and the throw is swallowed,
        /// so this ONLY RAISES A FLAG; Tick() does the real teardown on the main thread next frame.</summary>
        internal static void ResetAll()
        {
            _resetRequested = true;
        }

        /// <summary>MAIN THREAD ONLY — the actual teardown, reached from Tick().</summary>
        private static void ResetAllNow()
        {
            try
            {
                StopTracking("session reset");
                CityMap? map = null;
                try { var cm = InstanceBehavior<CityManager>.Instance; map = (cm != null) ? cm.cityMap : null; } catch { }
                foreach (var kv in _pins) DestroyPinObjects(kv.Value, map);
                _pins.Clear();
                _live.Clear();
                _gone.Clear();
                _cbcByKey.Clear();
                _nextCbcScan = 0f;
                _allHidden = false;
                // Rows are NEVER destroyed (there is no RemoveFilter - G8): they hide because nobody is online any
                // more. Tick returns on the !inSession path BEFORE its own row pass, so empty the set and re-apply
                // isAvailable here, or the rows would keep naming departed players until the scene ends.
                _online.Clear();
                _rowsDirty = false;
                try { if (_playersCategory != null) _playersCategory.UpdateFilterVisibility(); } catch { }
            }
            catch (Exception ex) { Warn("reset", ex); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Sprite? PlayerIcon()
        {
            try { return InstanceBehavior<UIs>.Instance.mapFilters.playerIcon; }
            catch { return null; }
        }

        private static bool SameColour(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        private static readonly Dictionary<string, CityBuildingController> _cbcByKey = new Dictionary<string, CityBuildingController>();
        private static float _nextCbcScan;

        /// <summary>Address key to its city building. GameStatePatcher.FindCBC does the same job but is private to
        /// that class, so the cache is repeated here; a MISS rescans at most every two seconds, so a key that
        /// never resolves cannot turn into a per-frame sweep of the whole city.</summary>
        private static CityBuildingController? FindBuilding(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return null;
            if (_cbcByKey.TryGetValue(addressKey, out var hit) && hit != null) return hit;

            float now;
            try { now = Time.unscaledTime; } catch { now = 0f; }
            if (now < _nextCbcScan) return null;
            _nextCbcScan = now + 2f;

            try
            {
                var cm = InstanceBehavior<CityManager>.Instance;
                var all = (cm != null) ? cm.cityBuildingControllers : null;
                if (all == null) return null;
                _cbcByKey.Clear();
                for (int i = 0; i < all.Length; i++)
                {
                    var cbc = all[i];
                    if (cbc == null) continue;
                    var reg = cbc.buildingRegistration;
                    if (reg == null) continue;
                    var k = GameStateReader.AddressKey(reg);
                    if (!string.IsNullOrEmpty(k)) _cbcByKey[k] = cbc;
                }
            }
            catch (Exception ex) { Warn("building scan", ex); return null; }

            return _cbcByKey.TryGetValue(addressKey, out var found) ? found : null;
        }

        private static float _nextWarn;

        /// <summary>Throttled: these paths run every frame, so one bad state must not fill the log.</summary>
        private static void Warn(string what, Exception ex)
        {
            try
            {
                float now = Time.unscaledTime;
                if (now < _nextWarn) return;
                _nextWarn = now + 5f;
                Plugin.Logger.LogWarning($"{Tag} {what}: {ex.Message}");
            }
            catch { }
        }
    }

    /// <summary>InitializeFilters rebuilds the whole panel from its templates (ClearClones at :188-191), so the
    /// mod's row has to be re-added every time it runs — a POSTFIX, never a one-shot at load.</summary>
    [HarmonyPatch(typeof(CityMapFilters), "InitializeFilters")]
    public static class Patch_CityMapFilters_PlayersFilter
    {
        static void Postfix(CityMapFilters __instance) => PlayerPins.InstallFilter(__instance);
    }
}
