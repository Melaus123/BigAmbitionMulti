using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using City.CityMap;   // CityMapFilter, CityMapFilterCategory
using Entities;       // InstanceBehavior
using UI;             // UIs

namespace BigAmbitionsMP
{
    /// <summary>PER-OWNER COLOURS, piece two (2026-09-05): a "Players" toggle in the city-map filter panel, and
    /// one live map pin per OTHER player in that player's slot colour.
    ///
    /// THE TOGGLE is a real CityMapFilter, created in a POSTFIX of CityMapFilters.InitializeFilters — that method
    /// starts by wiping every clone of its two templates (ClearClones, :188-191), so anything added earlier or
    /// once-only would be thrown away. It is moved to the FIRST row under the existing "Status" header (plan
    /// ruling: no new heading). Its on/off state persists exactly like a native filter, in the save's
    /// SelectedCitymapFilters list; the game's LoadFilters (:203-213) silently ignores names it does not know, so
    /// a save that has been touched by this mod still loads correctly without it.
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

        /// <summary>The one and only new on-screen string (user-approved 2026-09-05). It is both the filter's
        /// registry key and its label: CityMapFilter.SetUp does label.Key = filterName (:59) and the game renders
        /// a localisation key it does not know literally — the same behaviour the mod already relies on.</summary>
        internal const string FilterName = "Players";

        /// <summary>The key CreateLine gave the Status category (CityMapFilters.CreateStatusCategory :217).</summary>
        private const string StatusCategoryKey = "bizman_status";

        /// <summary>The Status category's first NATIVE filter. Fallback anchor for our sibling index, used only if
        /// the header line and the filter rows turn out not to share a parent (the prefab is not readable).</summary>
        private const string StatusFirstFilter = "buildingresume_rented_by_you";

        // ── The filter ────────────────────────────────────────────────────────

        /// <summary>Postfix body: create the "Players" filter and put it first under Status.</summary>
        internal static void InstallFilter(CityMapFilters filters)
        {
            try
            {
                if (filters == null) return;

                // CreateLine names each header clone after its localisation key (:353-355) and GetCategory
                // (:167-170) is the game's own name lookup over _filterCategories — no reflection needed.
                CityMapFilterCategory category = filters.GetCategory(StatusCategoryKey);
                if (category == null)
                {
                    Plugin.Logger.LogWarning($"{Tag} the '{StatusCategoryKey}' category was not found — no '{FilterName}' filter this session.");
                    return;
                }

                // Sprite = the icon the game uses for the local player's own map pin; data = null (legal, and the
                // game's own precedent at :218) so the filter is always available.
                CityMapFilter f = filters.CreateFilter(FilterName, filters.playerIcon, null, category);
                if (f == null)
                {
                    Plugin.Logger.LogWarning($"{Tag} CreateFilter returned nothing — no '{FilterName}' filter this session.");
                    return;
                }
                PlaceFirstUnderStatus(f, category);
            }
            catch (Exception ex)
            {
                try { Plugin.Logger.LogWarning($"{Tag} install filter: {ex.Message}"); } catch { }
            }
        }

        /// <summary>CityMapFilterCategory IS the header line clone (the MonoBehaviour sits on the lineEntry
        /// prefab), so the header's sibling index is the category's own. Headers and filters are appended to their
        /// separate templates' parents; when that parent is the same object, visual order is sibling order and
        /// "first under Status" = header index + 1. If the two parents differ, fall back to taking the place of
        /// the category's first native filter. If neither works, leave the row where the game appended it and say
        /// so in the log.</summary>
        private static void PlaceFirstUnderStatus(CityMapFilter f, CityMapFilterCategory category)
        {
            try
            {
                Transform ft = f.transform;
                Transform ht = category.transform;
                if (ft.parent != null && ht.parent != null && ft.parent == ht.parent)
                {
                    ft.SetSiblingIndex(ht.GetSiblingIndex() + 1);
                    Plugin.Logger.LogInfo($"{Tag} '{FilterName}' placed at the Status header index + 1 (now {ft.GetSiblingIndex()}).");
                    return;
                }

                Transform? firstNative = (ft.parent != null) ? ft.parent.Find(StatusFirstFilter) : null;
                if (firstNative != null)
                {
                    ft.SetSiblingIndex(firstNative.GetSiblingIndex());
                    Plugin.Logger.LogInfo($"{Tag} '{FilterName}' placed at the first native Status filter's index (now {ft.GetSiblingIndex()}) — header and filter rows do not share a parent.");
                    return;
                }

                Plugin.Logger.LogWarning($"{Tag} could not place '{FilterName}' under Status — it stays where the game appended it.");
            }
            catch (Exception ex)
            {
                try { Plugin.Logger.LogWarning($"{Tag} place filter: {ex.Message} — '{FilterName}' stays where the game appended it."); } catch { }
            }
        }

        /// <summary>Is the "Players" toggle ticked? The same call HousingMapCues already makes for a native filter
        /// (HousingMapCues.cs:168, __instance.IsFilterSelected("buildingresume_rented_by_you")).</summary>
        internal static bool PlayersFilterOn()
        {
            try { return InstanceBehavior<UIs>.Instance.mapFilters.IsFilterSelected(FilterName); }
            catch { return false; }
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
                    if (_pins.Count > 0) ResetAllNow();   // this branch already runs on the main thread
                    return;
                }

                // r2: with the map CLOSED or the "Players" toggle OFF nothing can be seen, so do no work at
                // all — no id list, no pin creation, no position maths. Every existing pin is hidden once and
                // _allHidden keeps every later frame free. A player who LEAVES while hidden therefore keeps a
                // (hidden) pin until the next frame with show == true, which removes it before anything shows.
                bool show = CityMap.IsOpen && PlayersFilterOn();
                if (!show)
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

                Sprite? icon = PlayerIcon();

                _live.Clear();
                var ids = RemotePlayerManager.GetRemotePlayerIds();
                if (ids != null)
                {
                    for (int i = 0; i < ids.Count; i++)
                    {
                        string pid = ids[i];
                        if (string.IsNullOrEmpty(pid) || pid == MPConfig.PlayerId) continue;   // never a pin for yourself
                        _live.Add(pid);
                        try { TickOne(pid, map, icon, show); }
                        catch (Exception ex) { Warn("pin '" + pid + "'", ex); }
                    }
                }

                if (_pins.Count > _live.Count)
                {
                    _gone.Clear();
                    foreach (var kv in _pins) if (!_live.Contains(kv.Key)) _gone.Add(kv.Key);
                    for (int i = 0; i < _gone.Count; i++) RemovePin(_gone[i], map);
                    _gone.Clear();
                }
            }
            catch (Exception ex) { Warn("tick", ex); }
        }

        private static void TickOne(string pid, CityMap map, Sprite? icon, bool show)
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

            p.Poi.SetHidden(!(show && placed));
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
                CityMap? map = null;
                try { var cm = InstanceBehavior<CityManager>.Instance; map = (cm != null) ? cm.cityMap : null; } catch { }
                foreach (var kv in _pins) DestroyPinObjects(kv.Value, map);
                _pins.Clear();
                _live.Clear();
                _gone.Clear();
                _cbcByKey.Clear();
                _nextCbcScan = 0f;
                _allHidden = false;
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
