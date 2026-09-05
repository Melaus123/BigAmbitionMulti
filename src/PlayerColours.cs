using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Buildings;      // BuildingRegistration
using Streets;        // Address.IsUndefined() extension (the same using DirectionGuider.cs has)
using Entities;       // InstanceBehavior

namespace BigAmbitionsMP
{
    /// <summary>PER-OWNER COLOURS, piece one (2026-09-05). Every surface that used to paint "someone else's,
    /// shared with you" in the single teal (HousingMapCues.SharedColor) now paints it in THAT owner's colour.
    /// A player is given a permanent colour SLOT by the host the first time they connect; the slot rides the
    /// session manifest keyed by StableId and is never reused. Unknown owner → the old teal (no regression);
    /// the local player's own things stay untinted. Each user may recolour any slot LOCALLY from the game's
    /// own Mods tab (Esc > Options) through the official OptionsService — a personal accessibility preference
    /// in the game's option prefs, never world state.
    ///
    /// NOTHING HERE MAY THROW INTO THE GAME: every entry point is wrapped and logs "[Colours] …".
    /// THREADING: Learn() is reached from the network poll thread (PlayerProfile / rivals snapshot), so it only
    /// raises a dirty flag; the repaint + the options re-registration are done by Tick() on the main thread
    /// (MPCanvasUI.Update), which also throttles them to at most once per frame.</summary>
    public static class PlayerColours
    {
        private const string Tag = "[Colours]";

        // ── Palette ───────────────────────────────────────────────────────────
        // Plan §3: every entry clears 4.5:1 on white; red (the game's "occupied") and yellow (the player's own
        // POIs) are excluded. The teal in use before this change survives as slot 3, darkened to #007F99.

        internal const int PaletteSize = 8;

        internal static readonly string[] ColourNames = { "violet", "blue", "teal", "magenta", "green", "purple", "brown", "olive" };

        internal static readonly Color32[] Palette =
        {
            new Color32(0xBF, 0x00, 0xFF, 0xFF),   // 1 violet
            new Color32(0x00, 0x6A, 0xFF, 0xFF),   // 2 blue
            new Color32(0x00, 0x7F, 0x99, 0xFF),   // 3 teal
            new Color32(0xE0, 0x00, 0x96, 0xFF),   // 4 magenta
            new Color32(0x00, 0x85, 0x37, 0xFF),   // 5 green
            new Color32(0x7F, 0x00, 0xFF, 0xFF),   // 6 purple
            new Color32(0xD1, 0x46, 0x00, 0xFF),   // 7 brown
            new Color32(0x60, 0x80, 0x00, 0xFF),   // 8 olive
        };

        /// <summary>The dropdown's choices: the eight palette names plus the generated/default colour.</summary>
        private static readonly string[] Choices = { "violet", "blue", "teal", "magenta", "green", "purple", "brown", "olive", "Auto" };

        /// <summary>The colour a slot has before any local override. Slots past the palette keep GENERATING
        /// (hue rotation with alternating lightness, plan §4) — defined for any positive slot, never throwing;
        /// colours simply get closer together. Slot 0 / negative = unknown holder = the old teal.</summary>
        internal static Color32 DefaultFor(int slot)
        {
            try
            {
                if (slot <= 0) return HousingMapCues.SharedColor;
                if (slot <= PaletteSize) return Palette[slot - 1];
                float h = ((slot - 1) * 0.618034f) % 1f;
                float v = (slot % 2 == 0) ? 0.55f : 0.70f;
                return Color.HSVToRGB(h, 0.85f, v);
            }
            catch (Exception ex) { Warn("default colour", ex); return HousingMapCues.SharedColor; }
        }

        // ── Session slot map (every machine): display PlayerId → slot ─────────

        private static readonly ConcurrentDictionary<string, int> _slotByPlayer = new();

        /// <summary>Record "this player holds this slot". Reached from the network thread, so it only raises the
        /// dirty flag — Tick() does the repaint and the options re-registration on the main thread.</summary>
        internal static void Learn(string playerId, int slot)
        {
            try
            {
                if (string.IsNullOrEmpty(playerId) || slot <= 0) return;
                bool changed = !_slotByPlayer.TryGetValue(playerId, out var had) || had != slot;
                _slotByPlayer[playerId] = slot;
                if (changed)
                {
                    Plugin.Logger.LogInfo($"{Tag} '{playerId}' holds slot {slot}.");
                    MarkDirty();
                }
            }
            catch (Exception ex) { Warn("learn", ex); }
        }

        /// <summary>The slot this player holds; 0 = not known on this machine.</summary>
        internal static int SlotOf(string playerId)
        {
            try { return (!string.IsNullOrEmpty(playerId) && _slotByPlayer.TryGetValue(playerId, out var s)) ? s : 0; }
            catch { return 0; }
        }

        /// <summary>Per-session state dies with the session (host start / client leaving a world).</summary>
        internal static void ResetSession()
        {
            try { _slotByPlayer.Clear(); _ownerByAddrKey.Clear(); MarkDirty(); }
            catch (Exception ex) { Warn("reset session", ex); }
        }

        // ── Local override (the game's own mod-option prefs) ──────────────────

        /// <summary>The mod id the official loader gave us — the first half of the game's pref key scheme.</summary>
        internal static string ModId = "";

        /// <summary>The game's OWN key scheme (ModOptionPrefs.BuildKey: "m:" + modId + ":" + optionId). The class
        /// is internal to the game, so the value is read straight out of PlayerPrefs with the same key.</summary>
        private static string PrefKey(int slot) => "m:" + ModId + ":colour_slot_" + slot;

        private static readonly ConcurrentDictionary<int, int> _overrides = new();

        /// <summary>-1 = no local choice, 0..7 = a palette index, 8 = "Auto" (the generated/default colour).</summary>
        internal static int OverrideIndexFor(int slot)
        {
            try
            {
                if (_overrides.TryGetValue(slot, out var v)) return v;
                if (string.IsNullOrEmpty(ModId)) return -1;
                int p = UnityEngine.PlayerPrefs.GetInt(PrefKey(slot), -1);
                _overrides[slot] = p;
                return p;
            }
            catch (Exception ex) { Warn("read override", ex); return -1; }
        }

        /// <summary>The Mods tab re-fires every dropdown's callback with its CURRENT value when the panel opens,
        /// so this must be idempotent. The game persists the pref itself; this only mirrors it in memory.</summary>
        internal static void SetOverride(int slot, int choiceIndex)
        {
            try { _overrides[slot] = choiceIndex; }
            catch (Exception ex) { Warn("set override", ex); }
        }

        /// <summary>The colour a slot shows on THIS machine: the local override when there is one, else the default.</summary>
        internal static Color32 ColourForSlot(int slot)
        {
            int o = OverrideIndexFor(slot);
            if (o >= 0 && o < PaletteSize) return Palette[o];
            return DefaultFor(slot);
        }

        // ── The lookup every painted surface uses ─────────────────────────────

        /// <summary>The colour for another player's thing. FALSE = do not tint (no owner, or it is the local
        /// player's own — those stay native). An owner whose slot this machine does not know yet keeps the old
        /// teal, so nothing regresses while the roster is still arriving.</summary>
        internal static bool TryColourFor(string ownerPlayerId, out Color32 c)
        {
            c = HousingMapCues.SharedColor;
            try
            {
                if (string.IsNullOrEmpty(ownerPlayerId)) return false;
                if (ownerPlayerId == MPConfig.PlayerId) return false;
                int slot = SlotOf(ownerPlayerId);
                if (slot <= 0) { c = HousingMapCues.SharedColor; return true; }
                c = ColourForSlot(slot);
                return true;
            }
            catch (Exception ex) { Warn("colour for", ex); c = HousingMapCues.SharedColor; return true; }
        }

        /// <summary>TMP rich-text opener in the owner's colour (the teal when the owner is unknown).</summary>
        internal static string TagOpen(string ownerPlayerId)
        {
            try
            {
                Color32 c = TryColourFor(ownerPlayerId, out var col) ? col : HousingMapCues.SharedColor;
                return "<color=#" + ColorUtility.ToHtmlStringRGB(c) + ">";
            }
            catch (Exception ex) { Warn("tag open", ex); return "<color=#" + ColorUtility.ToHtmlStringRGB(HousingMapCues.SharedColor) + ">"; }
        }

        // ── Residence/business owners on a client ─────────────────────────────
        // Clients keep no building→owner map; the host names the owner of every key it pushes in
        // PermissionBuildingAccess, so the map surfaces can tint per owner instead of one teal.

        private static readonly ConcurrentDictionary<string, string> _ownerByAddrKey = new();

        /// <summary>Replace-all per payload: the host's push is the whole truth for the keys it covers.</summary>
        internal static void LearnOwners(IDictionary<string, string> owners)
        {
            try
            {
                _ownerByAddrKey.Clear();
                if (owners == null) return;
                foreach (var kv in owners)
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value)) _ownerByAddrKey[kv.Key] = kv.Value;
                if (_ownerByAddrKey.Count > 0) Plugin.Logger.LogInfo($"{Tag} owners for {_ownerByAddrKey.Count} shared address key(s).");
                MarkDirty();
            }
            catch (Exception ex) { Warn("learn owners", ex); }
        }

        internal static string OwnerOfAddressKey(string key)
        {
            try { return (!string.IsNullOrEmpty(key) && _ownerByAddrKey.TryGetValue(key, out var o)) ? o : ""; }
            catch { return ""; }
        }

        /// <summary>Colour for a building by its address key. No owner known → the old teal (true).</summary>
        internal static bool TryColourForAddressKey(string key, out Color32 c)
        {
            c = HousingMapCues.SharedColor;
            try
            {
                string owner = OwnerOfAddressKey(key);
                if (string.IsNullOrEmpty(owner)) { c = HousingMapCues.SharedColor; return true; }
                return TryColourFor(owner, out c);
            }
            catch (Exception ex) { Warn("colour for key", ex); c = HousingMapCues.SharedColor; return true; }
        }

        // ── HOST: permanent slot assignment (StableId → slot, manifest-backed) ─

        private static readonly Dictionary<string, int> _slotByStable = new();
        private static readonly object _stableLock = new object();

        /// <summary>A new HOSTED world starts with an empty permanent table (colours r2, MINOR-3).</summary>
        internal static void ResetHost() { lock (_stableLock) { _slotByStable.Clear(); } }

        /// <summary>Seed from the loaded manifest (clear + copy). A returning player keeps their slot forever.</summary>
        internal static void HostSeed(IDictionary<string, int> fromManifest)
        {
            try
            {
                lock (_stableLock)
                {
                    _slotByStable.Clear();
                    if (fromManifest != null)
                        foreach (var kv in fromManifest)
                            if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0) _slotByStable[kv.Key] = kv.Value;
                }
                Plugin.Logger.LogInfo($"{Tag} host seeded {(fromManifest?.Count ?? 0)} colour slot(s) from the manifest.");
                MarkDirty();
            }
            catch (Exception ex) { Warn("host seed", ex); }
        }

        /// <summary>The permanent slot for this stable id — the one it already has, or the next one ever issued.
        /// Slots are NEVER reused, so a departed player's colour is not handed to someone else.</summary>
        internal static int HostAssign(string stableId)
        {
            try
            {
                if (string.IsNullOrEmpty(stableId)) return 0;
                lock (_stableLock)
                {
                    if (_slotByStable.TryGetValue(stableId, out var had) && had > 0) return had;
                    int max = 0;
                    foreach (var v in _slotByStable.Values) if (v > max) max = v;
                    int next = max + 1;
                    _slotByStable[stableId] = next;
                    Plugin.Logger.LogInfo($"{Tag} host assigned colour slot {next} (stable id ending '{(stableId.Length > 6 ? stableId.Substring(stableId.Length - 6) : stableId)}').");
                    return next;
                }
            }
            catch (Exception ex) { Warn("host assign", ex); return 0; }
        }

        /// <summary>A copy of the host's permanent table, for the save manifest.</summary>
        internal static Dictionary<string, int> HostSnapshot()
        {
            try { lock (_stableLock) return new Dictionary<string, int>(_slotByStable); }
            catch (Exception ex) { Warn("host snapshot", ex); return new Dictionary<string, int>(); }
        }

        // ── Repaint + the Mods-tab picker ─────────────────────────────────────

        /// <summary>Repaint the surfaces that do not redraw themselves: the city-map POIs. Everything else
        /// (staff rows, shop cards, dropdown rows, the designer panel) repaints on its next draw.</summary>
        internal static void RefreshTints()
        {
            try { HousingMapCues.RefreshSharedPois(); }
            catch (Exception ex) { Warn("refresh tints", ex); }
        }

        /// <summary>Publish the "Multiplayer" section into the game's own Mods tab (Esc &gt; Options): one dropdown
        /// per slot that has a KNOWN holder, labelled with that player's live display name. Empty slots are not
        /// listed. Re-registering is how the labels follow the roster — the game fires its OnChanged and rebuilds.</summary>
        internal static void RegisterOptions()
        {
            try
            {
                if (string.IsNullOrEmpty(ModId)) return;

                // slot → display name, ascending, only where a holder is known.
                var holders = new SortedDictionary<int, string>();
                foreach (var kv in _slotByPlayer)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0 || holders.ContainsKey(kv.Value)) continue;
                    if (kv.Key == MPConfig.PlayerId) continue;   // review r1 MINOR-5: no row for yourself - your own things are never tinted
                    string nm = ResolveName(kv.Key);
                    if (string.IsNullOrWhiteSpace(nm)) continue;
                    holders[kv.Value] = nm;
                }
                if (MPServer.IsRunning)
                {
                    // The host also knows slots of players who are not connected right now — but only ones whose
                    // display id is still on record can be NAMED, and an unnameable row would be meaningless.
                    Dictionary<string, int> stable;
                    lock (_stableLock) stable = new Dictionary<string, int>(_slotByStable);
                    var pidByStable = new Dictionary<string, string>();
                    try
                    {
                        foreach (var kv in MPServer.StableIdByPlayer)
                            if (!string.IsNullOrEmpty(kv.Value)) pidByStable[kv.Value] = kv.Key;
                    }
                    catch { }
                    foreach (var kv in stable)
                    {
                        if (kv.Value <= 0 || holders.ContainsKey(kv.Value)) continue;
                        if (!pidByStable.TryGetValue(kv.Key, out var pid) || string.IsNullOrEmpty(pid)) continue;
                        if (pid == MPConfig.PlayerId) continue;   // review r1 MINOR-5: the host's own slot is not a row either
                        string nm = ResolveName(pid);
                        if (string.IsNullOrWhiteSpace(nm)) continue;
                        holders[kv.Value] = nm;
                    }
                }

                if (holders.Count == 0)
                {
                    // review r1 NOTE-11: nobody else to colour - take the section out of the Mods tab entirely
                    // rather than publishing a header with no rows under it.
                    try { BigAmbitions.Mods.OptionsService.RemoveModOptions(ModId); } catch { }
                    return;
                }

                var o = new BigAmbitions.Mods.ModOptions();
                o.AddHeader("Multiplayer");
                foreach (var kv in holders)
                {
                    int n = kv.Key;
                    o.AddDropdown("colour_slot_" + n, "Slot " + n + " – " + kv.Value, Choices,
                                  (n <= PaletteSize) ? n - 1 : PaletteSize,
                                  v => { SetOverride(n, v); MarkTintDirty(); });   // review r1 MINOR-8: the Mods tab re-fires every dropdown on open - one sweep per frame, not one per row
                }
                BigAmbitions.Mods.OptionsService.Register(ModId, o);
                Plugin.Logger.LogInfo($"{Tag} Mods tab: registered {holders.Count} slot picker(s).");
            }
            catch (Exception ex) { Warn("register options", ex); }
        }

        private static string ResolveName(string playerId)
        {
            try { return MPNames.Resolve(playerId); } catch { return ""; }
        }

        // ── Main-thread tick (MPCanvasUI.Update) ──────────────────────────────

        private static int _dirty;

        private static void MarkDirty() { try { Interlocked.Exchange(ref _dirty, 1); } catch { } }

        /// <summary>Repaint-only request (a local colour override changed): no options re-registration.</summary>
        private static int _tintDirty;

        private static void MarkTintDirty() { try { Interlocked.Exchange(ref _tintDirty, 1); } catch { } }

        /// <summary>MAIN THREAD. Consumes at most one repaint + re-registration per frame, so a burst of
        /// roster messages on the network thread can never storm the map or the options service.</summary>
        internal static void Tick()
        {
            try
            {
                bool roster = Interlocked.Exchange(ref _dirty, 0) != 0;
                bool tint   = Interlocked.Exchange(ref _tintDirty, 0) != 0;
                if (!roster && !tint) return;
                RefreshTints();               // review r1 MINOR-8: at most ONE map sweep per frame, whichever flag asked for it
                if (roster) RegisterOptions();
            }
            catch (Exception ex) { Warn("tick", ex); }
        }

        private static void Warn(string what, Exception ex)
        {
            try { Plugin.Logger.LogWarning($"{Tag} {what}: {ex.Message}"); } catch { }
        }
    }

    /// <summary>NEW SITE (piece one): the DIRECTIONS pin. The guider repaints its icon with the building's own
    /// background colour; when the building belongs to another SESSION player, paint it in that player's colour
    /// instead. AI rivals are never tinted.</summary>
    [HarmonyPatch(typeof(UI.Guiders.DirectionGuider), nameof(UI.Guiders.DirectionGuider.UpdatePoiIcon))]
    public static class Patch_DirectionGuider_UpdatePoiIcon_OwnerColour
    {
        static void Postfix(UI.Guiders.DirectionGuider __instance, Address address)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (__instance == null || __instance.poi == null) return;
                // review r1 MAJOR-2: GuidersManager.UpdateGuidersWithAddress calls UpdatePoiIcon(address) on EVERY guider and the native
                // body no-ops unless this guider's CurrentAddress == address (DirectionGuider.cs:112-119) - mirror that guard.
                var cur = __instance.CurrentAddress;
                if (cur == null || cur.IsUndefined() || cur != address) return;
                var reg = Helpers.BuildingHelper.GetBuildingRegistration(address);
                if (reg == null) return;
                string owner = "";
                try { owner = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                bool fromMap = false;
                if (owner.Length == 0)
                {
                    owner = PlayerColours.OwnerOfAddressKey(GameStateReader.AddressKey(address));
                    fromMap = owner.Length > 0;
                }
                if (!fromMap && !GameStatePatcher.IsSessionPlayerRivalId(owner)) return;   // never an AI rival
                if (PlayerColours.TryColourFor(owner, out var c)) __instance.poi.SetIcon(reg.GetPOIIcon(), c);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Colours] directions pin: {ex.Message}"); }
        }
    }

    /// <summary>NEW SITE (piece one): the interior designer's business-type icon. Another session player's
    /// building shows their colour; anything else keeps the native white.</summary>
    [HarmonyPatch(typeof(UI.InteriorDesigner.BusinessTypeInfoPanelUI), "OnBusinessTypeChanged")]
    public static class Patch_BusinessTypeInfoPanel_OwnerColour
    {
        private static Color? _designerIconDefault;

        static void Postfix(UI.InteriorDesigner.BusinessTypeInfoPanelUI __instance)
        {
            try
            {
                if (__instance == null || __instance.businessTypeImage == null) return;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;   // review r1 MINOR-7: never touch single-player
                if (_designerIconDefault == null) _designerIconDefault = __instance.businessTypeImage.color;   // the prefab's own tint, captured once
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                string owner = "";
                try { owner = reg?.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                __instance.businessTypeImage.color =
                    (GameStatePatcher.IsSessionPlayerRivalId(owner) && PlayerColours.TryColourFor(owner, out var c)) ? (Color)c : _designerIconDefault.Value;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Colours] designer business-type icon: {ex.Message}"); }
        }
    }
}
