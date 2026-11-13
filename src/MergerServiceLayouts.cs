using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Blueprints;
using BusinessLayoutSets;
using HarmonyLib;
using Newtonsoft.Json;

namespace BigAmbitionsMP
{
    /// <summary>T-24 part 2 (v28): INTERIOR LAYOUTS HELD BY THE MOD for installations booked through a merged company.
    ///
    /// The game's interior firm loads a blueprint by NAME on install day (InteriorInstallationFirmContract.DoInstallation
    /// :44-81 -> BlueprintsFolderLoader.GetBlueprint :61-69 / BlueprintsLayoutHelper.GetLayoutFromBlueprint :72-80, which
    /// read persistentDataPath/Blueprints/&lt;name&gt;/Layout.json). A booking routed from a partner ("mergerinterior"), and
    /// an owner's own blueprint install handed to a stand-in (bug B), name a file that is on ANOTHER machine. So the
    /// layout travels as mod data (PwLayout - never raw JSON: the game's TypeJsonConverter builds whatever type a file
    /// names), is validated here, and held per booking with this machine's per-save state.
    ///
    /// T-24 fold 2 - THE MARK LIVES INSIDE THE GAME'S OWN CONTRACT. A routed or handed-over blueprint contract's
    /// designName is rewritten to the reserved form "bamp:held/&lt;booking&gt;/&lt;design&gt;". It contains '/', and the
    /// game's blueprint names are folder names (BlueprintsFolderLoader.LoadLightBlueprintFromFolder :215-218:
    /// name = Path.GetFileName(folderPath), the only source GetBlueprint searches, :63/:177-192), which can never
    /// contain '/'. So the mark rides every save the game writes (coordinated save, rollback slots, the offline fork,
    /// the hand-over and hand-back lists), and wherever the held layout is missing the game's OWN by-name lookup fails
    /// -> HandleLayoutNotFound (:110-116, couldnt_find_layout: contract removed, nothing charged). It can never reach a
    /// same-named file on this machine's disk. The install hooks below resolve the form to the held layout; the one
    /// place the game shows a contract's designName to a player (InteriorInstallationContractsList.SetUpContract :32 ->
    /// SetEntryInfo :43) is given the design name back. isCompatBlueprint is forced false on every marked contract (it
    /// means a free install tied to one character; cancelling one reads GetBlueprint(..).Result.metadata with no null
    /// check, InteriorInstallationFirmAgentDialog.cs:185-189).</summary>
    internal static class MergerServiceLayouts
    {
        internal const int MaxItems = 2000;
        internal const int MaxBytes = 256 * 1024;

        // Owner decisions 22/25 (2026-11-13): the approved wording, exact.
        internal const string TextBlueprintBad  = "That blueprint can't be used for your partner's building.";
        internal const string TextBlueprintMods = "That blueprint needs mods your partner doesn't have.";
        internal const string TextNotPlaced     = "Your partner's shop couldn't take that order, so it wasn't placed.";

        // ── The mark ──

        internal const string MarkPrefix = "bamp:held/";

        internal static bool IsMarked(string? name) => name != null && name.StartsWith(MarkPrefix, StringComparison.Ordinal);

        internal static string MarkedName(string bookingId, string design) => MarkPrefix + bookingId + "/" + (design ?? "");

        /// <summary>The booking a marked name holds ("" for an unmarked name).</summary>
        internal static string BookingOf(string? name)
        {
            if (!IsMarked(name)) return "";
            int i = name!.IndexOf('/', MarkPrefix.Length);
            return i < 0 ? name.Substring(MarkPrefix.Length) : name.Substring(MarkPrefix.Length, i - MarkPrefix.Length);
        }

        /// <summary>The design name a player picked: a marked name mapped back, any other name unchanged.</summary>
        internal static string DisplayName(string? name)
        {
            if (!IsMarked(name)) return name ?? "";
            int i = name!.IndexOf('/', MarkPrefix.Length);
            return i < 0 ? "" : name.Substring(i + 1);
        }

        internal class Held
        {
            public string    BookingId   { get; set; } = "";
            public string    AddressKey  { get; set; } = "";
            public string    DesignName  { get; set; } = "";   // the design name as the player picked it (never the marked form)
            public bool      Native      { get; set; }         // the owner's own unmarked install, marked only on a stand-in: hand-back returns it unmarked
            public bool      OwnerCompat { get; set; }         // the owner's own free-install flag, returned at hand-back (never used here)
            public PwLayout? Layout      { get; set; }
        }

        /// <summary>T-24 slice 1B (owner decision 26): a booking a co-member ROUTED to this machine - who booked it, so their own
        /// manage-contracts list shows it and only they cancel it. In play the record is BOUND to the game's contract object
        /// (_bound); the game's delivery, moving and installation contracts carry no id field, so across a save the contract is
        /// found again by its stable FINGERPRINT (SharedShopWorkTabs.RoutedFingerprint: fields the game never changes) and its
        /// place among identical contracts in the save written beside this file (Ord). A record is dropped only when its bound
        /// contract leaves the game's own list, or when the loaded save holds no such contract.</summary>
        internal class RoutedRecord
        {
            public string BookingId { get; set; } = "";
            public string Booker    { get; set; } = "";
            public string Kind      { get; set; } = "";
            public string Fp        { get; set; } = "";
            public int    Ord       { get; set; } = -1;
        }

        private class HeldFile
        {
            public List<Held> Entries { get; set; } = new List<Held>();
            public List<RoutedRecord> Routed { get; set; } = new List<RoutedRecord>();   // T-24 slice 1B (decision 26)
            public List<string> Delivered { get; set; } = new List<string>();            // T-24 slice 1B: held job messages already shown here
        }

        // T-24 fold 2: the held layouts are PER-SAVE state in a file BESIDE THE .hsg of every slot this machine's
        // coordinated save writes (MPSaveCoordinator.PerformLocalSave -> SaveWith(the slot's character folder)), so a
        // carried-forward slot carries it with the .hsg (CarryForwardAbsentMembers copies the whole folder). At every
        // world load it is replaced by the file beside the .hsg actually LOADED - the selected variant/checkpoint, not
        // the lineage base (MPSaveCoordinator.RestoreCargoMarksNow -> RestoreFor, on the main thread). A contract whose
        // held entry is missing still carries its mark, so it fails safe; it never reads this machine's disk.
        private const string HeldFileName = "service-layouts.bamp.json";
        private static readonly Dictionary<string, Held> _held = new Dictionary<string, Held>(StringComparer.Ordinal);

        /// <summary>Our own DTOs only, and never the game's (or any global) converters or type names.</summary>
        private static JsonSerializer Serializer()
            => JsonSerializer.Create(new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 32 });

        private static string Ser(object o)
        {
            var sb = new StringBuilder();
            using (var w = new StringWriter(sb)) Serializer().Serialize(w, o);
            return sb.ToString();
        }

        private static T? De<T>(string json) where T : class
        {
            using (var r = new JsonTextReader(new StringReader(json))) return Serializer().Deserialize<T>(r);
        }

        /// <summary>SAVE, both roles, main thread: this machine's held layouts go beside the .hsg just written.</summary>
        internal static void SaveWith(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                string p = Path.Combine(folder, HeldFileName);
                if (_held.Count == 0 && _routed.Count == 0 && _deliveredOrder.Count == 0) { if (File.Exists(p)) File.Delete(p); return; }
                // T-24 slice 1B: each bound record's place in the save just written (a record still waiting for its re-link
                // keeps the place it was read back with).
                if (!RelinkPending)
                    foreach (var r in _routed) { var c = Bound(r.BookingId); r.Ord = c == null ? -1 : SharedShopWorkTabs.RoutedOrdinal(c); }
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, Ser(new HeldFile { Entries = new List<Held>(_held.Values), Routed = new List<RoutedRecord>(_routed), Delivered = new List<string>(_deliveredOrder) }));
                File.Copy(tmp, p, true);
                File.Delete(tmp);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] held interior layouts could not be saved beside '{folder}': {ex.Message}"); }
        }

        /// <summary>WORLD LOAD, both roles, MAIN THREAD: the held layouts beside the loaded .hsg replace the live store
        /// ("" = a fresh character: none).</summary>
        internal static void RestoreFor(string folder)
        {
            _held.Clear();
            ResetAcked();
            _routed.Clear();
            _bound.Clear();
            RelinkPending = false;
            _delivered.Clear();
            _deliveredOrder.Clear();
            try
            {
                if (string.IsNullOrEmpty(folder)) return;
                string p = Path.Combine(folder, HeldFileName);
                if (!File.Exists(p)) return;
                var hf = De<HeldFile>(File.ReadAllText(p));
                if (hf?.Entries != null)
                    foreach (var e in hf.Entries) if (e != null && !string.IsNullOrEmpty(e.BookingId)) _held[e.BookingId] = e;
                if (hf?.Routed != null)
                    foreach (var r in hf.Routed) if (r != null && !string.IsNullOrEmpty(r.BookingId) && !string.IsNullOrEmpty(r.Booker) && !string.IsNullOrEmpty(r.Fp)) _routed.Add(r);
                if (hf?.Delivered != null)
                    foreach (var d in hf.Delivered) if (!string.IsNullOrEmpty(d) && _delivered.Add(d)) _deliveredOrder.Add(d);
                RelinkPending = _routed.Count > 0;   // bound at the world-ready edge (SharedShopWorkTabs.RelinkRouted), never against the old world
                if (_routed.Count > 0 || _deliveredOrder.Count > 0)
                    Plugin.Logger.LogInfo($"[Merger] routed bookings read back from '{folder}': {_routed.Count} (held job messages already shown: {_deliveredOrder.Count}).");
                Plugin.Logger.LogInfo($"[Merger] held interior layouts read back from '{folder}': {_held.Count}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] held interior layouts could not be read back: {ex.Message}"); }
        }

        /// <summary>The held entry a contract's mark names (null: unmarked, or marked with nothing held).</summary>
        internal static Held? Find(Entities.InteriorInstallationFirmContract? c)
            => c == null || !c.isBlueprint ? null : FindByName(c.designName);

        private static Held? FindByName(string? name)
        {
            string id = BookingOf(name);
            return id.Length > 0 && _held.TryGetValue(id, out var e) ? e : null;
        }

        /// <summary>MARK a blueprint contract already in this machine's list: its designName takes the reserved form
        /// for `bookingId`, it is never a free install, and the layout (null = none: the game's couldnt_find_layout path
        /// at install) is held for it. Entries no live contract names any more are dropped.</summary>
        internal static void Mark(Entities.InteriorInstallationFirmContract ic, string bookingId, string design, bool native, bool ownerCompat, PwLayout? layout)
        {
            string id = (bookingId ?? "").Replace('/', '_');
            if (id.Length == 0) id = "bk-" + Guid.NewGuid().ToString("N");
            string name = DisplayName(design);
            string key = GameStateReader.AddressKey(ic.addressToDoTheInstallation);
            ic.designName = MarkedName(id, name);
            ic.isCompatBlueprint = false;
            _held[id] = new Held { BookingId = id, AddressKey = key, DesignName = name, Native = native, OwnerCompat = ownerCompat, Layout = layout };
            Prune();
            Plugin.Logger.LogInfo($"[Merger] interior layout held for '{key}' ('{name}', booking {id}, "
                                + (layout == null ? "NO layout - the install takes the game's couldnt_find_layout path)" : $"{layout.Items?.Count ?? 0} item(s))"));
        }

        private static void Prune()
        {
            try
            {
                var list = SaveGameManager.Current?.interiorInstallationFirmContracts;
                if (list == null) return;
                var live = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in list) if (c != null && c.isBlueprint && IsMarked(c.designName)) live.Add(BookingOf(c.designName));
                var gone = new List<string>();
                foreach (var k in _held.Keys) if (!live.Contains(k)) gone.Add(k);
                foreach (var k in gone) _held.Remove(k);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] held interior layouts prune: {ex.Message}"); }
        }

        // ── T-24 slice 1B (owner decision 26): who booked each ROUTED booking held here (per-save, beside the .hsg) ──

        private static readonly List<RoutedRecord> _routed = new List<RoutedRecord>();
        private static readonly Dictionary<string, object> _bound = new Dictionary<string, object>(StringComparer.Ordinal);   // booking -> the game's contract (in play only)

        /// <summary>Records not yet bound: set by RestoreFor (read back with a save) and by RecordRoutedUnbound (a bundle
        /// received before the world was ready), cleared by the re-link at the world-ready edge. While set nothing is
        /// pruned, sent or cancelled.</summary>
        internal static bool RelinkPending;

        internal static int RoutedCount => _routed.Count;

        internal static List<RoutedRecord> RoutedRecords() => new List<RoutedRecord>(_routed);

        internal static object? Bound(string? id) => id != null && _bound.TryGetValue(id, out var c) ? c : null;

        internal static void Bind(string id, object c) { if (!string.IsNullOrEmpty(id) && c != null) _bound[id] = c; }

        internal static bool IsBoundObject(object c)
        {
            foreach (var v in _bound.Values) if (ReferenceEquals(v, c)) return true;
            return false;
        }

        /// <summary>The record bound to this contract object (null = not a routed booking).</summary>
        internal static RoutedRecord? RecordOf(object? c)
        {
            if (c == null) return null;
            foreach (var kv in _bound)
                if (ReferenceEquals(kv.Value, c)) return _routed.Find(r => r.BookingId == kv.Key);
            return null;
        }

        internal static void RecordRouted(string bookingId, string booker, string kind, object contract)
        {
            if (string.IsNullOrEmpty(bookingId) || string.IsNullOrEmpty(booker) || contract == null) return;
            string fp = SharedShopWorkTabs.RoutedFingerprint(contract);
            if (fp.Length == 0) return;
            _routed.RemoveAll(r => r.BookingId == bookingId);
            _routed.Add(new RoutedRecord { BookingId = bookingId, Booker = booker, Kind = kind, Fp = fp });
            _bound[bookingId] = contract;
        }

        /// <summary>A record whose contract came in a hand-over/hand-back bundle before the world was ready: kept UNBOUND (no
        /// saved place: Ord -1) and bound at the world-ready re-link (SharedShopWorkTabs.RelinkRouted), never against a world
        /// still loading.</summary>
        internal static void RecordRoutedUnbound(string bookingId, string booker, string kind, string fp)
        {
            if (string.IsNullOrEmpty(bookingId) || string.IsNullOrEmpty(booker) || string.IsNullOrEmpty(fp)) return;
            _routed.RemoveAll(r => r.BookingId == bookingId);
            _bound.Remove(bookingId);
            _routed.Add(new RoutedRecord { BookingId = bookingId, Booker = booker, Kind = kind, Fp = fp });
            RelinkPending = true;
        }

        internal static void DropRouted(ICollection<string> ids, string why)
        {
            if (ids == null || ids.Count == 0) return;
            int n = _routed.RemoveAll(r => ids.Contains(r.BookingId));
            foreach (var id in ids) _bound.Remove(id);
            if (n > 0) Plugin.Logger.LogInfo($"[Merger] {n} routed booking record(s) closed ({why}): {string.Join(", ", ids)}.");
        }

        /// <summary>A routed blueprint installation cancelled on its runner: its held layout goes with it (as F4 below).</summary>
        internal static void DropHeldFor(Entities.InteriorInstallationFirmContract c)
        {
            var e = Find(c);
            if (e != null) Drop(e);
        }

        // T-24 slice 1B fold: the held job messages (CompanyMessages.DeliverHeld) already shown on this machine. Kept with the
        // per-save data, so it is exactly as old as the game's own contact messages it guards: a restart before the next save
        // loses both together, and a restart after it keeps both - never shown twice.
        private const int DeliveredCap = 1000;
        private static readonly HashSet<string> _delivered = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> _deliveredOrder = new List<string>();

        /// <summary>True the first time this save sees `messageId` (and records it); false after.</summary>
        internal static bool FirstDelivery(string messageId)
        {
            if (string.IsNullOrEmpty(messageId) || !_delivered.Add(messageId)) return false;
            _deliveredOrder.Add(messageId);
            if (_deliveredOrder.Count > DeliveredCap) { _delivered.Remove(_deliveredOrder[0]); _deliveredOrder.RemoveAt(0); }
            return true;
        }

        private static void Drop(Held e) => _held.Remove(e.BookingId);

        // ── A layout's content hash (the bundles carry it; the body travels once) ──

        private sealed class HashBox { public string H = ""; }
        private static readonly ConditionalWeakTable<PwLayout, HashBox> _hashOf = new ConditionalWeakTable<PwLayout, HashBox>();

        internal static string HashOfJson(string json)
        {
            using (var sha = SHA256.Create())
            {
                byte[] b = sha.ComputeHash(Encoding.UTF8.GetBytes(json ?? ""));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }

        internal static string HashOf(PwLayout d) => _hashOf.GetValue(d, x => new HashBox { H = HashOfJson(Ser(x)) }).H;

        // ── Conversion: the game's layout <-> the mod's DTO ──

        private static PwVec3 V(SerializableVector3 v) => new PwVec3 { X = v.x, Y = v.y, Z = v.z };

        internal static PwLayout ToDto(BusinessLayoutSet set, string buildingType, string buildingSize, int buildingVersion, List<string>? metaMods)
        {
            var d = new PwLayout
            {
                BuildingType = buildingType ?? "", BuildingSize = buildingSize ?? "", BuildingVersion = buildingVersion,
                SetBusinessType = set.BusinessType ?? "", SetBuildingSize = set.BuildingSize ?? "", SetBuildingVersion = set.BuildingVersion,
                LayoutName = set.LayoutName ?? "", BuildNumber = set.buildNumber,
            };
            var mods = new HashSet<string>(StringComparer.Ordinal);
            if (set.requiredModIds != null) foreach (var m in set.requiredModIds) if (!string.IsNullOrEmpty(m)) mods.Add(m);
            if (metaMods != null) foreach (var m in metaMods) if (!string.IsNullOrEmpty(m)) mods.Add(m);
            d.RequiredModIds = new List<string>(mods);
            if (set.Items != null)
                foreach (var it in set.Items)
                {
                    if (it == null) continue;
                    var q = it.rotation;
                    var o = new PwLayoutItem
                    {
                        Id = it.id, ItemName = it.itemName, ParentId = it.parentId, CustomValue = it.customValue,
                        WorldSpaceTextValue = it.worldSpaceTextValue, LinkedItemName = it.linkedItemName,
                        Px = it.position.x, Py = it.position.y, Pz = it.position.z, Rw = q.w, Rx = q.x, Ry = q.y, Rz = q.z,
                    };
                    var ps = it.playerItemPurchaserSettings;
                    if (ps != null) o.Purchaser = new PwLayoutPurchaser { Name = ps.name, Enabled = ps.enabled, ItemName = ps.itemName, ItemQuantity = ps.itemQuantity };
                    if (it.stackedItems != null)
                    {
                        o.StackedItems = new List<PwLayoutChild>();
                        foreach (var s in it.stackedItems)
                            if (s != null) o.StackedItems.Add(new PwLayoutChild { ChildId = s.childId, ChildItemName = s.childItemName, AttachmentIndex = s.attachmentIndex });
                    }
                    if (it.dirtSpotsThatAffects != null) o.DirtSpots = new List<int>(it.dirtSpotsThatAffects);
                    if (it.customPositions != null) { o.CustomPositions = new List<PwVec3>(); foreach (var cp in it.customPositions) o.CustomPositions.Add(V(cp)); }
                    if (it.customColors != null)
                    {
                        o.CustomColors = new List<CustomColorInfo>();
                        foreach (var cc in it.customColors) if (cc != null) o.CustomColors.Add(new CustomColorInfo { Channel = (int)cc.channel, ColorPacked = cc.color.color });
                    }
                    if (it.dirtAffectedCells != null) { o.DirtCells = new List<PwCell>(); foreach (var c in it.dirtAffectedCells) o.DirtCells.Add(new PwCell { X = c.x, Z = c.z }); }
                    if (it is FactoryItem f)
                    {
                        o.IsFactory = true; o.SelectedRecipeId = f.selectedRecipeId; o.WorkstationType = f.workstationType;
                        o.Priority = f.priority; o.ProduceUpTo = f.produceUpTo; o.ProduceUpToValue = f.produceUpToValue;
                    }
                    d.Items.Add(o);
                }
            if (set.interiorDesigns != null)
                foreach (var sd in set.interiorDesigns)
                {
                    if (sd == null) continue;
                    var di = new PwLayoutDesign { UUID = sd.UUID };
                    if (sd.materials != null)
                        foreach (var m in sd.materials)
                            di.Materials.Add(new PwLayoutMaterial { MaterialID = m.MaterialID, MaterialIndex = m.MaterialIndex, ColorIndex = m.ColorIndex, Ignore = m.ignore });
                    d.InteriorDesigns.Add(di);
                }
            return d;
        }

        internal static BusinessLayoutSet ToLayoutSet(PwLayout d)
        {
            var set = new BusinessLayoutSet
            {
                BusinessType = d.SetBusinessType, BuildingSize = d.SetBuildingSize, BuildingVersion = d.SetBuildingVersion,
                LayoutName = d.LayoutName, buildNumber = d.BuildNumber,
                requiredModIds = d.RequiredModIds != null ? new List<string>(d.RequiredModIds) : new List<string>(),
            };
            if (d.Items != null)
                foreach (var o in d.Items)
                {
                    if (o == null) continue;
                    Item it = o.IsFactory
                        ? new FactoryItem { selectedRecipeId = o.SelectedRecipeId, workstationType = o.WorkstationType, priority = o.Priority,
                                            produceUpTo = o.ProduceUpTo, produceUpToValue = o.ProduceUpToValue }
                        : new Item();
                    it.id = o.Id; it.itemName = o.ItemName; it.parentId = o.ParentId; it.customValue = o.CustomValue;
                    it.worldSpaceTextValue = o.WorldSpaceTextValue; it.linkedItemName = o.LinkedItemName;
                    it.position = new SerializableVector3 { x = o.Px, y = o.Py, z = o.Pz };
                    it.rotation = new SerializableQuaternion { w = o.Rw, x = o.Rx, y = o.Ry, z = o.Rz };
                    if (o.Purchaser != null)
                        it.playerItemPurchaserSettings = new BigAmbitions.Items.PlayerItemPurchaserSettings
                        { name = o.Purchaser.Name, enabled = o.Purchaser.Enabled, itemName = o.Purchaser.ItemName, itemQuantity = o.Purchaser.ItemQuantity };
                    it.stackedItems = new List<AttachableChild>();
                    if (o.StackedItems != null)
                        foreach (var s in o.StackedItems)
                            if (s != null) it.stackedItems.Add(new AttachableChild { childId = s.ChildId, childItemName = s.ChildItemName, attachmentIndex = s.AttachmentIndex });
                    it.dirtSpotsThatAffects = o.DirtSpots != null ? new List<int>(o.DirtSpots) : new List<int>();
                    if (o.CustomPositions != null)
                    {
                        it.customPositions = new List<SerializableVector3>();
                        foreach (var cp in o.CustomPositions) if (cp != null) it.customPositions.Add(new SerializableVector3 { x = cp.X, y = cp.Y, z = cp.Z });
                    }
                    if (o.CustomColors != null)
                    {
                        it.customColors = new List<BigAmbitions.Items.CustomColor>();
                        foreach (var cc in o.CustomColors)
                            if (cc != null) it.customColors.Add(new BigAmbitions.Items.CustomColor { channel = (BigAmbitions.Items.CustomColorChannel)cc.Channel, color = new SerializableColor(cc.ColorPacked) });
                    }
                    if (o.DirtCells != null)
                    {
                        it.dirtAffectedCells = new List<CellPosition>();
                        foreach (var c in o.DirtCells) if (c != null) it.dirtAffectedCells.Add(new CellPosition { x = c.X, z = c.Z });
                    }
                    set.Items.Add(it);
                }
            if (d.InteriorDesigns != null)
                foreach (var di in d.InteriorDesigns)
                {
                    if (di == null) continue;
                    int n = di.Materials?.Count ?? 0;
                    // materials must be a non-null array (InteriorElement.Deserialize reads .Length with no null check).
                    var arr = new SerializedInteriorDesign.SerializableInteriorMaterial[n];
                    for (int i = 0; i < n; i++)
                    {
                        var m = di.Materials![i];
                        arr[i] = new SerializedInteriorDesign.SerializableInteriorMaterial
                        { MaterialID = m?.MaterialID ?? "", MaterialIndex = m?.MaterialIndex ?? 0, ColorIndex = m?.ColorIndex ?? 0, ignore = m?.Ignore ?? false };
                    }
                    set.interiorDesigns.Add(new SerializedInteriorDesign { UUID = di.UUID, materials = arr });
                }
            return set;
        }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        internal static int SerializedBytes(PwLayout d)
        {
            var sb = new StringBuilder();
            using (var w = new StringWriter(sb)) Serializer().Serialize(w, d);
            return Encoding.UTF8.GetByteCount(sb.ToString());
        }

        /// <summary>The runner's checks on a layout for the building at reg: building type, size and version match
        /// (the native picker's own test, InteriorInstallationFirmHelper.GetBlueprintNames :78-104); every item name passes
        /// the game's ValidateLayout (CompatibilityBlueprintValidator :32-48 - an item it would strip is a refusal); every
        /// position and rotation is a finite number; at most MaxItems items and MaxBytes serialized; and (checkMods) every
        /// required mod is present here (Blueprint.IsMissingMods :175-181). Codes: "bpbad" / "bpmods".</summary>
        internal static bool Validate(PwLayout? d, BuildingRegistration reg, bool checkMods, out string code, out string why)
        {
            code = "bpbad"; why = "";
            try
            {
                if (d == null || d.Items == null) { why = "no layout carried."; return false; }
                if (d.Items.Count > MaxItems) { why = $"{d.Items.Count} items, the limit is {MaxItems}."; return false; }
                int bytes = SerializedBytes(d);
                if (bytes > MaxBytes) { why = $"{bytes} bytes serialized, the limit is {MaxBytes}."; return false; }
                string btype = reg.BuildingCached.BuildingType;
                var si = new BuildingSizeInfo(reg);
                if (d.BuildingType != btype || d.BuildingSize != si.buildingSize || d.BuildingVersion != si.buildingVersion)
                { why = $"made for {d.BuildingType} {d.BuildingSize} v{d.BuildingVersion}, the building is {btype} {si.buildingSize} v{si.buildingVersion}."; return false; }
                foreach (var o in d.Items)
                {
                    if (o == null) { why = "an empty item row."; return false; }
                    bool ok = Finite(o.Px) && Finite(o.Py) && Finite(o.Pz) && Finite(o.Rw) && Finite(o.Rx) && Finite(o.Ry) && Finite(o.Rz);
                    if (ok && o.CustomPositions != null) foreach (var cp in o.CustomPositions) ok &= cp != null && Finite(cp.X) && Finite(cp.Y) && Finite(cp.Z);
                    if (!ok) { why = $"item '{o.ItemName}' has a position that is not a finite number."; return false; }
                }
                var set = ToLayoutSet(d);
                int before = set.Items.Count;
                Player.SaveSystem.CompatibilityFixes.CompatibilityBlueprintValidator.ValidateLayout(set);
                if (set.Items.Count != before) { why = $"{before - set.Items.Count} item name(s) are not valid in this game."; return false; }
                if (checkMods && d.RequiredModIds != null && d.RequiredModIds.Count > 0
                    && !BigAmbitions.ModsInternal.ModDiscoveryRegistry.IsDiscovered(d.RequiredModIds))
                { code = "bpmods"; why = $"needs mod(s) not present here: {string.Join(",", d.RequiredModIds)}."; return false; }
                code = ""; return true;
            }
            catch (Exception ex) { why = $"the layout could not be read: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        /// <summary>THIS machine's own blueprint, by name, as the DTO - read through the game's own Blueprint.GetLayout
        /// (Blueprint.cs:126-136: ApplyCompatibilityFixes, which runs the legacy-id fixes UpdateLegacyIds /
        /// UpdateWorkstationLegacyIds, then LoadBlueprintLayout), unvalidated so Validate can count stripped items.
        /// GetLayout is asynchronous and every caller decides synchronously, so the read is STARTED here (or by Prewarm
        /// when the member picks the design) and answered from its finished task; a read still running is answered with
        /// null and a reason. A read that FAILED (faulted or cancelled) is logged and forgotten, so the next call - the
        /// next pick - starts it again. Cached per file and write time.</summary>
        internal static PwLayout? ReadOwnBlueprint(string name, out string why)
        {
            why = "";
            try
            {
                var t = BlueprintsFolderLoader.GetBlueprint(name);
                if (!t.IsCompleted) { why = "the blueprint list is still loading."; return null; }
                var bp = t.IsFaulted || t.IsCanceled ? null : t.Result;
                if (bp == null || bp.metadata == null) { why = $"no blueprint '{name}' here."; return null; }
                string path = bp.GetLayoutPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { why = $"blueprint '{name}' has no layout file."; return null; }
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_ownCache.TryGetValue(name, out var hit) && hit.path == path && hit.stamp == stamp && hit.dto != null) return hit.dto;
                if (!_ownReads.TryGetValue(name, out var rd) || rd.path != path || rd.stamp != stamp)
                {
                    rd = (path, stamp, bp.GetLayout(false));
                    _ownReads[name] = rd;
                }
                if (!rd.task.IsCompleted) { why = $"blueprint '{name}' is still being read."; return null; }
                _ownReads.Remove(name);   // finished either way: a failed read is started afresh by the next call
                if (rd.task.IsFaulted || rd.task.IsCanceled)
                {
                    why = $"blueprint '{name}' could not be read ({(rd.task.IsCanceled ? "cancelled" : rd.task.Exception?.GetBaseException().Message ?? "faulted")}).";
                    Plugin.Logger.LogWarning($"[Merger] {why} It is read again at the next pick.");
                    return null;
                }
                var set = rd.task.Result;
                if (set == null) { why = $"blueprint '{name}' could not be read."; return null; }
                var dto = ToDto(set, bp.metadata.buildingType, bp.metadata.buildingSizeInfo?.buildingSize ?? "", bp.metadata.buildingSizeInfo?.buildingVersion ?? 0,
                                bp.metadata.requiredModIds);
                _ownCache[name] = (path, stamp, dto);
                return dto;
            }
            catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; return null; }
        }
        private static readonly Dictionary<string, (string path, DateTime stamp, PwLayout dto)> _ownCache
            = new Dictionary<string, (string path, DateTime stamp, PwLayout dto)>(StringComparer.Ordinal);
        private static readonly Dictionary<string, (string path, DateTime stamp, Task<BusinessLayoutSet> task)> _ownReads
            = new Dictionary<string, (string path, DateTime stamp, Task<BusinessLayoutSet> task)>(StringComparer.Ordinal);

        /// <summary>The member picked a blueprint in the installation dialog: start reading it now, so the commit finds
        /// it read (InteriorInstallationFirmDesignSettings.OnSelectDesign :192-197). Only while a merger is present.</summary>
        [HarmonyPatch(typeof(UI.Dialog.InteriorInstallationFirmDesignSettings), "OnSelectDesign")]
        public static class Patch_InteriorDesign_Prewarm
        {
            static void Postfix(UI.Dialog.InteriorInstallationFirmDesignSettings __instance)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0) return;
                    if (__instance != null && __instance.isBlueprint && !string.IsNullOrEmpty(__instance.designName)) ReadOwnBlueprint(__instance.designName, out _);
                }
                catch { }
            }
        }

        // ── The paperwork bundle and the absent-owner hand-over (bug B) ──

        private static readonly HashSet<string> _handOverLogged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>SENDER, main thread (PaperworkSync.Build): one blueprint contract's bundle row. DesignName is the
        /// design as the player picked it; Routed says the contract is MARKED here for a reason that is not "my own file"
        /// (routed from a partner, or handed over to this stand-in by someone whose file it is), so whoever receives it
        /// marks it again. LayoutHash names the layout; the body (Layout) rides only while the host is not known to hold
        /// that hash. A stand-in never reads its own disk for a building it only runs.</summary>
        internal static void FillHandOver(PwInstallContract pc, Entities.InteriorInstallationFirmContract c)
        {
            pc.DesignName = DisplayName(c.designName);
            pc.IsCompatBlueprint = c.isCompatBlueprint;
            pc.Routed = false; pc.LayoutHash = ""; pc.Layout = null;
            if (!c.isBlueprint) return;
            try
            {
                string key = GameStateReader.AddressKey(c.addressToDoTheInstallation);
                PwLayout? d = null;
                string why = "";
                if (IsMarked(c.designName))
                {
                    var e = Find(c);
                    pc.Routed = e == null || !e.Native;
                    if (e != null && e.Native) pc.IsCompatBlueprint = e.OwnerCompat;   // hand-back: the owner's own flag
                    d = e?.Layout;
                    if (d == null) why = "it is marked here and holds no layout.";
                }
                else if (!MergerAbsence.SimulatesHere(key))
                {
                    d = ReadOwnBlueprint(c.designName, out why);
                    if (d != null && (d.Items.Count > MaxItems || SerializedBytes(d) > MaxBytes)) { why = "over the size limit."; d = null; }
                }
                else why = "a stand-in never reads its own disk for a building it only runs.";
                if (d == null)
                {
                    if (_handOverLogged.Add(key + "|" + pc.DesignName))
                        Plugin.Logger.LogWarning($"[Merger] the blueprint install at '{key}' ('{pc.DesignName}') is published with no layout: {why}");
                    return;
                }
                pc.LayoutHash = HashOf(d);
                if (!HostHolds(pc.LayoutHash)) pc.Layout = d;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] hand-over row: {ex.Message}"); }
        }

        /// <summary>T-24 fold F2 (backstop): the paperwork bundle is over PaperworkSync.MaxBundleBytes by `over` bytes.
        /// Layout BODIES are left out, largest first, until the estimate fits with headroom (the hash stays, so a host
        /// that already holds it loses nothing; one it does not hold is sent again with a later bundle, because only a
        /// receipted body counts as held). False when no body is left to take out.</summary>
        internal static bool TrimLayouts(BusinessPaperworkPayload p, int over, string why)
        {
            var list = p?.Lists?.InteriorInstallationFirmContracts;
            if (list == null) return false;
            var carried = new List<(PwInstallContract c, int bytes)>();
            foreach (var c in list) if (c?.Layout != null) carried.Add((c, SerializedBytes(c.Layout)));
            if (carried.Count == 0) return false;
            carried.Sort((x, y) => y.bytes.CompareTo(x.bytes));
            int need = over + 16 * 1024, freed = 0;
            foreach (var (c, b) in carried)
            {
                if (freed >= need) break;
                c.Layout = null;
                freed += b;
                Plugin.Logger.LogWarning($"[Merger] the layout body of the blueprint install at '{c.AddressKey}' ('{c.DesignName}', {b} bytes) is left out of "
                                       + $"this paperwork bundle ({why}): the bundle is over its {PaperworkSync.MaxBundleBytes}-byte cap. Until the host holds it, "
                                       + "a stand-in takes the game's couldnt_find_layout path for it (contract removed, nothing charged).");
            }
            return true;
        }

        // ── SENDER: which bodies the host holds (T-24 fold 2, bandwidth) ──
        // A body rides the first bundle after it appears; the host's RECEIPT of that bundle (PaperworkSync.OnHostReceipt,
        // matched by bundle number) makes it HELD, and later bundles carry the hash alone. HostTakeBodies keeps the body
        // even when the bundle itself is refused over the cap. Forgotten on every connect and every world load, so a new
        // host (or a rolled-back one) is sent each body once again. The host itself checks its own store.

        private static readonly HashSet<string> _hostHas = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<KeyValuePair<int, List<string>>> _awaiting = new List<KeyValuePair<int, List<string>>>();

        private static bool HostHolds(string h)
        {
            if (string.IsNullOrEmpty(h)) return false;
            if (MPServer.IsRunning) lock (_bodies) return _bodies.ContainsKey(h);
            return _hostHas.Contains(h);
        }

        /// <summary>SENDER, main thread: bundle number `seq` went out; the bodies it carries await its receipt.</summary>
        internal static void NoteSent(BusinessPaperworkPayload p, int seq)
        {
            try
            {
                var list = p?.Lists?.InteriorInstallationFirmContracts;
                if (list == null) return;
                var hs = new List<string>();
                foreach (var c in list) if (c?.Layout != null && !string.IsNullOrEmpty(c.LayoutHash)) hs.Add(c.LayoutHash);
                if (hs.Count == 0) return;
                _awaiting.Add(new KeyValuePair<int, List<string>>(seq, hs));
                while (_awaiting.Count > 64) _awaiting.RemoveAt(0);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] layout bodies sent: {ex.Message}"); }
        }

        /// <summary>SENDER, main thread: the host receipted bundle `seq` (and every older one): their bodies are held.</summary>
        internal static void OnReceipt(int seq)
        {
            for (int i = _awaiting.Count - 1; i >= 0; i--)
            {
                if (_awaiting[i].Key > seq) continue;
                foreach (var h in _awaiting[i].Value)
                    if (_hostHas.Add(h)) Plugin.Logger.LogInfo($"[Merger] the host holds interior layout {h} (bundle #{_awaiting[i].Key}) - later bundles carry its hash only.");
                _awaiting.RemoveAt(i);
            }
        }

        internal static void ResetAcked() { _hostHas.Clear(); _awaiting.Clear(); }

        // ── HOST: layout bodies by hash, kept in the manifest (MpManifest.PaperworkLayouts) ──
        // Stored bundles carry hashes only. A body is kept while any stored bundle names its hash (checked at every
        // manifest write); the hand-over and the hand-back are filled from here (MPServer.PaperworkJsonForStable).

        private static readonly Dictionary<string, string> _bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _missingLogged = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Regex HashRef = new Regex("\"LayoutHash\":\"([0-9a-f]{32})\"", RegexOptions.Compiled);

        /// <summary>HOST, main thread, first thing for every bundle stored: the bodies it carries go to the store by
        /// their content hash and leave the bundle.</summary>
        internal static void HostTakeBodies(BusinessPaperworkPayload p, string senderPid)
        {
            try
            {
                var list = p?.Lists?.InteriorInstallationFirmContracts;
                if (list == null) return;
                foreach (var c in list)
                {
                    if (c == null) continue;
                    if (c.Layout != null)
                    {
                        string json = Ser(c.Layout);
                        string h = HashOfJson(json);
                        if (!string.IsNullOrEmpty(c.LayoutHash) && c.LayoutHash != h)
                            Plugin.Logger.LogWarning($"[Merger] interior layout from '{senderPid}' for '{c.AddressKey}' named {c.LayoutHash} but its content hashes to {h} - stored under {h}.");
                        c.LayoutHash = h;
                        c.Layout = null;
                        bool added;
                        lock (_bodies) { added = !_bodies.ContainsKey(h); if (added) _bodies[h] = json; }
                        if (added) Plugin.Logger.LogInfo($"[Merger] interior layout {h} stored for '{c.AddressKey}' ('{c.DesignName}', from '{senderPid}', {Encoding.UTF8.GetByteCount(json)} bytes).");
                    }
                    else if (!string.IsNullOrEmpty(c.LayoutHash))
                    {
                        bool have; lock (_bodies) have = _bodies.ContainsKey(c.LayoutHash);
                        if (!have && _missingLogged.Add(c.LayoutHash))
                            Plugin.Logger.LogWarning($"[Merger] the bundle from '{senderPid}' names interior layout {c.LayoutHash} ('{c.AddressKey}') which this host does not hold - "
                                                   + "a stand-in given that install takes the game's couldnt_find_layout path (contract removed, nothing charged).");
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] layout bodies from '{senderPid}': {ex.Message}"); }
        }

        /// <summary>HOST: a stored bundle's text with every named layout body filled back in from the store, for the
        /// hand-over to a stand-in and the hand-back to a returning owner. Unchanged when it names none.</summary>
        internal static string HostFillBodies(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json) || !HashRef.IsMatch(json)) return json;
                var p = JsonConvert.DeserializeObject<BusinessPaperworkPayload>(json);
                var list = p?.Lists?.InteriorInstallationFirmContracts;
                if (list == null) return json;
                int filled = 0;
                foreach (var c in list)
                {
                    if (c == null || c.Layout != null || string.IsNullOrEmpty(c.LayoutHash)) continue;
                    string? body; lock (_bodies) _bodies.TryGetValue(c.LayoutHash, out body);
                    if (body == null)
                    {
                        Plugin.Logger.LogWarning($"[Merger] hand-over of the blueprint install at '{c.AddressKey}' ('{c.DesignName}'): this host does not hold layout {c.LayoutHash} - "
                                               + "the receiver takes the game's couldnt_find_layout path (contract removed, nothing charged).");
                        continue;
                    }
                    c.Layout = De<PwLayout>(body);
                    if (c.Layout != null) filled++;
                }
                return filled == 0 ? json : JsonConvert.SerializeObject(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] hand-over layout bodies: {ex.Message}"); return json; }
        }

        /// <summary>HOST, any thread (a manifest write): the bodies some stored bundle still names; the rest are dropped.</summary>
        internal static List<MpLayoutBody> HostSnapshotBodies()
        {
            var outList = new List<MpLayoutBody>();
            try
            {
                var named = new HashSet<string>(StringComparer.Ordinal);
                var store = MPServer.PaperworkStore;
                lock (store)
                    foreach (var e in store.Values)
                        foreach (Match m in HashRef.Matches(e?.Text ?? "")) named.Add(m.Groups[1].Value);   // T-54 fold F3: through the one read
                lock (_bodies)
                {
                    var gone = new List<string>();
                    foreach (var k in _bodies.Keys) if (!named.Contains(k)) gone.Add(k);
                    foreach (var k in gone) _bodies.Remove(k);
                    foreach (var kv in _bodies) outList.Add(new MpLayoutBody { Hash = kv.Key, Json = kv.Value });
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] layout bodies for the manifest: {ex.Message}"); }
            return outList;
        }

        /// <summary>HOST, world load: the loaded manifest's bodies replace the store.</summary>
        internal static void HostRestoreBodies(List<MpLayoutBody>? list)
        {
            HostResetBodies();
            if (list == null) return;
            lock (_bodies)
                foreach (var b in list)
                    if (b != null && !string.IsNullOrEmpty(b.Hash) && !string.IsNullOrEmpty(b.Json)) _bodies[b.Hash] = b.Json;
        }

        internal static void HostResetBodies()
        {
            lock (_bodies) _bodies.Clear();
            _missingLogged.Clear();
        }

        /// <summary>RECEIVER, main thread: a handed-over blueprint install was just added to this machine's list (with the
        /// bundle's plain design name). A STAND-IN marks every one: the owner's own install as Native (hand-back returns
        /// it unmarked, with the owner's own free-install flag), a routed one as routed. The OWNER, getting its installs
        /// back, marks a Routed one again and leaves its own native (its own file, its own character). A missing layout
        /// is held as missing: the game's couldnt_find_layout path, never this machine's disk.</summary>
        internal static void OnHandedOver(object item, PwInstallContract c, bool standIn)
        {
            try
            {
                if (c == null || !c.IsBlueprint || !(item is Entities.InteriorInstallationFirmContract ic)) return;
                string design = DisplayName(c.DesignName);
                if (!standIn && !c.Routed) { ic.designName = design; return; }
                var d = c.Layout;
                if (d != null && !string.IsNullOrEmpty(c.LayoutHash))
                {
                    if (HashOf(d) != c.LayoutHash)
                        Plugin.Logger.LogWarning($"[Merger] hand-over layout for '{c.AddressKey}' does not match its hash {c.LayoutHash} - held as carried.");
                    else if (!MPServer.IsRunning) _hostHas.Add(c.LayoutHash);   // the host filled it from its own store
                }
                Mark(ic, "ho-" + Guid.NewGuid().ToString("N"), design, native: !c.Routed, ownerCompat: c.IsCompatBlueprint, d);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] hand-over hold: {ex.Message}"); }
        }

        // ── The install hooks: a MARKED name resolves to the held layout, and nothing else ──

        [HarmonyPatch(typeof(Entities.InteriorInstallationFirmContract), "DoInstallation")]
        public static class Patch_InteriorInstall_HeldLayout
        {
            static void Prefix(Entities.InteriorInstallationFirmContract __instance)
            {
                try
                {
                    if (__instance == null || !__instance.isBlueprint || !IsMarked(__instance.designName)) return;
                    __instance.isCompatBlueprint = false;
                    string key = GameStateReader.AddressKey(__instance.addressToDoTheInstallation);
                    var e = Find(__instance);
                    if (e != null)
                        Plugin.Logger.LogInfo($"[Merger] interior install at '{key}' uses the held layout ('{e.DesignName}', booking {e.BookingId}).");
                    else
                        Plugin.Logger.LogWarning($"[Merger] interior install at '{key}' ('{DisplayName(__instance.designName)}', booking {BookingOf(__instance.designName)}) "
                                               + "is marked and nothing is held for it - the game's couldnt_find_layout path: contract removed, nothing charged.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] held layout install (prefix): {ex.Message}"); }
            }

            // DoInstallation is async void: this runs when it first yields. Both hooks below answer a marked name with
            // completed tasks, so the load has happened by then; the contract has left the list unless the install waits
            // (player inside), and only then is the held entry kept.
            static void Postfix(Entities.InteriorInstallationFirmContract __instance)
            {
                try
                {
                    if (__instance == null || !__instance.isBlueprint || !IsMarked(__instance.designName)) return;
                    var e = Find(__instance);
                    var list = SaveGameManager.Current?.interiorInstallationFirmContracts;
                    if (e != null && list != null && !list.Contains(__instance)) Drop(e);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] held layout install (postfix): {ex.Message}"); }
            }
        }

        /// <summary>A marked name is never a blueprint on this disk (it cannot be a folder name): answered null without the
        /// game's list scan - exactly what the game's own lookup would return.</summary>
        [HarmonyPatch(typeof(BlueprintsFolderLoader), "GetBlueprint")]
        public static class Patch_GetBlueprint_HeldLayout
        {
            static bool Prefix(string __0, ref Task<Blueprint> __result)
            {
                if (!IsMarked(__0)) return true;
                __result = Task.FromResult<Blueprint>(null!);
                return false;
            }
        }

        [HarmonyPatch(typeof(BlueprintsLayoutHelper), "GetLayoutFromBlueprint")]
        public static class Patch_GetLayoutFromBlueprint_HeldLayout
        {
            static bool Prefix(string __0, ref Task<BusinessLayoutSet> __result)
            {
                if (!IsMarked(__0)) return true;
                var e = FindByName(__0);
                BusinessLayoutSet? set = null;
                try
                {
                    if (e?.Layout != null)
                    {
                        set = ToLayoutSet(e.Layout);
                        Player.SaveSystem.CompatibilityFixes.CompatibilityBlueprintValidator.ValidateLayout(set);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] held layout could not be built: {ex.Message}"); set = null; }
                if (set == null)
                    Plugin.Logger.LogWarning($"[Merger] held layout for '{e?.AddressKey ?? "?"}' ('{DisplayName(__0)}', booking {BookingOf(__0)}) is MISSING - "
                                           + "the game's couldnt_find_layout path: contract removed, nothing charged.");
                __result = Task.FromResult<BusinessLayoutSet>(set!);
                return false;
            }
        }

        /// <summary>The one place the game shows a contract's designName to a player - the firm's contract list entry
        /// (InteriorInstallationContractsList.SetUpContract :32 -> SetEntryInfo :41-44): a marked name shows the design
        /// name the player picked. (The firm agent's "contract set" messages, InteriorInstallationFirmAgentDialog :151,
        /// are only sent for a contract that dialog itself creates - never a marked one.)</summary>
        [HarmonyPatch(typeof(Dialogs.InteriorInstallationContractsList), "SetEntryInfo")]
        public static class Patch_InteriorContractsList_DisplayName
        {
            static void Prefix(ref string __3)
            {
                if (IsMarked(__3)) __3 = DisplayName(__3);
            }
        }

        /// <summary>T-24 fold F4: the player cancels an installation (InteriorInstallationFirmAgentDialog.OnCancelInstallation
        /// :179-189): its held entry goes with it.</summary>
        [HarmonyPatch(typeof(Dialogs.InteriorInstallationFirmAgentDialog), "OnCancelInstallation")]
        public static class Patch_InteriorCancel_DropHeld
        {
            static void Prefix(Entities.InteriorInstallationFirmContract __0)
            {
                try
                {
                    var e = Find(__0);
                    if (e == null) return;
                    Drop(e);
                    Plugin.Logger.LogInfo($"[Merger] interior installation at '{e.AddressKey}' cancelled - its held layout ('{e.DesignName}', booking {e.BookingId}) dropped.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] held layout cancel: {ex.Message}"); }
            }
        }

        // ── T-24 fold F3: the charge, as evidence (every installation and move on this machine) ──
        // The money moved by the game's own charge call alone, measured around that call. Its SIGN is the game's: a move
        // bills movingFee + per-item fee - the selling price of the destination's old items and vehicles
        // (MovingServiceHelper.PayMove :219-249), an installation fee + layout prices - the old items' selling price, and
        // charges nothing at all at a price of 0 or less (InteriorInstallationFirmContract.TryPayInstallation :152-155).

        [HarmonyPatch(typeof(Entities.InteriorInstallationFirmContract), "HandleLayoutNotFound")]
        public static class Patch_InteriorInstall_LayoutNotFound
        {
            static void Prefix(Entities.InteriorInstallationFirmContract __instance)
            {
                try
                {
                    Plugin.Logger.LogWarning($"[Merger] interior install at '{GameStateReader.AddressKey(__instance.addressToDoTheInstallation)}' "
                                           + $"('{DisplayName(__instance.designName)}', blueprint={__instance.isBlueprint}) took the game's couldnt_find_layout path: contract removed, nothing charged.");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Entities.InteriorInstallationFirmContract), "TryPayInstallation")]
        public static class Patch_InteriorInstall_Charge
        {
            static void Prefix(out float __state) { __state = SaveGameManager.Current?.Money ?? 0f; }
            static void Postfix(Entities.InteriorInstallationFirmContract __instance, float __0, bool __result, float __state)
            {
                try
                {
                    float now = SaveGameManager.Current?.Money ?? 0f;
                    Plugin.Logger.LogInfo($"[Merger] interior installation at '{GameStateReader.AddressKey(__instance.addressToDoTheInstallation)}' charged "
                                        + $"${(__state - now).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} "
                                        + $"(price {__0.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, paid={__result}, compat={__instance.isCompatBlueprint}).");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Buildings.BuildingTypes.Special.MovingCompany.MovingServiceHelper), "PayMove")]
        public static class Patch_MovingService_Charge
        {
            static void Prefix(out float __state) { __state = SaveGameManager.Current?.Money ?? 0f; }
            static void Postfix(bool __result, float __state)
            {
                try
                {
                    float now = SaveGameManager.Current?.Money ?? 0f;
                    string from = "";
                    try { from = GameStateReader.AddressKey(Buildings.BuildingTypes.Special.MovingCompany.MovingServiceHelper.FromAddress); } catch { }
                    Plugin.Logger.LogInfo($"[Merger] moving service from '{from}' charged ${(__state - now).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} (paid={__result}).");
                }
                catch { }
            }
        }
    }
}
