using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Reflection;
using Buildings;
using Helpers;
using Entities;
using Streets;

namespace BigAmbitionsMP
{
    /// <summary>T-24 part 2 slice 2 (2026-11-13; protocol 28, unreleased - additive): the HOST's record of ONE contents
    /// hand-over between two players' games (MessageType.ContentsHandover). Purpose "move" = the game's moving service
    /// between buildings run by DIFFERENT games of one merged company (owner decision 24 B); "sale" is reserved for T-52.
    /// Written into the manifest (MpManifest.Handovers) the moment any leg changes and kept until the hand-over is CLOSED
    /// and both saves cover it. State: offer (the destination asked; the origin was told to release) -> dropped (the
    /// origin gave everything up; the host holds the package) -> applied (the destination's game ran the game's own move)
    /// | return (the destination could not: the package goes home) -> closed (the origin confirmed its last leg).
    /// Charged / ChargeStamped: the destination's game took the game's own bill (PayMove), recorded in the SAME manifest
    /// write that stamps that party's cash (the T-50 money-once rule) - a replay never bills again.</summary>
    public class MpHandoverEntry
    {
        public string HandoverId   { get; set; } = "";
        public string Purpose      { get; set; } = "move";
        public string State        { get; set; } = "offer";
        public string Outcome      { get; set; } = "";      // "" | moved | returned
        public string OriginPid    { get; set; } = "";
        public string OriginStable { get; set; } = "";
        public string DestPid      { get; set; } = "";
        public string DestStable   { get; set; } = "";
        public string OriginKey    { get; set; } = "";
        public string DestKey      { get; set; } = "";
        public string CompanyKey   { get; set; } = "";
        public bool   Transfer     { get; set; }
        public int    Day          { get; set; }
        public string PackageJson  { get; set; } = "";       // the host's copy, kept until closed
        public bool   Charged      { get; set; }
        public bool   ChargeStamped { get; set; }
        public bool   Deferred     { get; set; }            // the destination's player stood inside: the next offer re-delivers
        // F2 (decision 57 a): consecutive release ERRORS of this move (the origin could not pack or drop). Kept in the manifest with
        // the record (State "held" between two move hours); a successful release ends the record's held state, the 2nd error
        // cancels the move (State "cancel" until the destination confirms).
        public int    ErrorHolds   { get; set; }
        public int    Attempt      { get; set; }            // the release round (one per move hour); resends carry the same one
        public int    HeldDay      { get; set; }            // the game day the record went "held" - an unanswered hold expires
        public bool   OriginClosed { get; set; }
        public bool   OriginSaved  { get; set; }
        public bool   DestSaved    { get; set; }
    }

    /// <summary>What crosses: every record as the game's own SAVE holds it - its serialized FIELDS (ContentsHandover.FieldsOnly), so ids,
    /// cargo, slots and drivers are exactly what the origin's save held.</summary>
    public class HandoverPackage
    {
        public List<string> Items    { get; set; } = new();   // every ItemInstance at the origin (children included)
        public List<string> Vehicles { get; set; } = new();   // every VehicleInstance the origin's game had parked there
        public List<string> Slots    { get; set; } = new();   // the origin warehouse's VehicleSlots (vehicle + driver), in order
        public HandoverBusiness? Business { get; set; }       // transfer settings only (owner decision 49 b)
    }

    public class HandoverBusiness
    {
        public Dictionary<string, string> Fields { get; set; } = new();          // BizManTransfer's registration fields
        public List<string> Staff { get; set; } = new();                          // the real EmployeeInstances at the origin
        public Dictionary<string, List<string>> Lists { get; set; } = new();      // entries OWNED by the origin, per game list
    }

    internal static class ContentsHandover
    {
        private const float ResendSeconds = 5f;
        // T-24 slice 2 fold (run 20261113-202441): the game's save is OdinSerializer BINARY (SaveGameSerializationHelper.cs:58-71,
        // SerializeValue) - it writes an object's serialized FIELDS, never a property. A plain Newtonsoft pass also calls every public
        // getter, and some compute from live data: FactoryWorkstationInstance.CreatedItemName => SelectedRecipe.output.item
        // (FactoryWorkstationInstance.cs:65; SelectedRecipe is null with no recipe chosen, :45-61) threw on every release of a
        // factory. So every game record crosses as its fields only, by the rule the game's serializer uses (FieldsOnly).
        private static readonly JsonSerializerSettings GameJson = new JsonSerializerSettings
        { ReferenceLoopHandling = ReferenceLoopHandling.Ignore, TypeNameHandling = TypeNameHandling.Auto, ContractResolver = new FieldsOnly() };

        /// <summary>The fields a Unity/Odin serializer writes: public instance fields, and non-public ones marked [SerializeField] /
        /// [OdinSerialize]; never [NonSerialized] or readonly fields, never a property. A field hidden by a derived class's field
        /// of the same name is taken once (the most derived). Collections and dictionaries keep Newtonsoft's own handling.</summary>
        private sealed class FieldsOnly : DefaultContractResolver
        {
            protected override List<MemberInfo> GetSerializableMembers(Type objectType)
            {
                var list = new List<MemberInfo>(); var names = new HashSet<string>(StringComparer.Ordinal);
                for (var ty = objectType; ty != null && ty != typeof(object); ty = ty.BaseType)
                    foreach (var f in ty.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.IsNotSerialized || f.IsInitOnly) continue;                                  // [NonSerialized], readonly
                        if (f.IsDefined(typeof(ObsoleteAttribute), true)) continue;                       // the game's save skips them (SaveGameSerializationPolicy.cs:14-17)
                        bool marked = f.GetCustomAttributes(true).Any(a => { string n = a.GetType().Name; return n == "SerializeField" || n == "OdinSerializeAttribute"; });
                        if (f.Name.Contains("<") && !marked) continue;                                    // a compiler backing field only when [field: SerializeField]
                        if (!f.IsPublic && !marked) continue;
                        if (names.Add(f.Name)) list.Add(f);
                    }
                return list;
            }

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var p = base.CreateProperty(member, memberSerialization);
                p.Readable = true; p.Writable = true;
                return p;
            }
        }

        /// <summary>The package wrapper is the mod's own type (properties): plain Newtonsoft, as it was written.</summary>
        private static HandoverPackage? DePkg(string s) { try { return JsonConvert.DeserializeObject<HandoverPackage>(s); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Handover] package unreadable: {ex.Message}"); return null; } }

        // BizManTransfer.Transfer :23-43 - the registration fields the game's transfer copies to the destination.
        private static readonly string[] BizFields =
        { "BusinessName", "businessTypeName", "logoSettings", "signAppearanceSettings", "uniformsBySkill",
          "retailPrices", "storedRetailPrices", "scheduleDays", "marketingCampaigns", "deliveredItems" };

        // The game lists whose entries BELONG to a building (the field naming it): they live on the game that runs it, so
        // they cross with the business. An entry of another headquarters that only POINTS at the origin stays where it is
        // and is re-pointed there at close, by the game's own rules (BizManTransfer :102-187).
        private static readonly (string List, string Field)[] OwnedLists =
        {
            ("DeliveryContracts", "businessAddress"), ("FurnitureDeliveryContracts", "toAddress"), ("FoodDeliveryContracts", "toAddress"),
            ("importPartnerships", "headquartersAddress"), ("logisticsManagerPlans", "headquartersAddress"),
            ("pricingManagerPlans", "headquartersAddress"), ("hrManagerPlans", "headquartersAddress"), ("headhunterPlans", "headquartersAddress"),
        };

        private static readonly object _lock = new();
        private static readonly List<MpHandoverEntry> _list = new();                      // HOST, under _lock
        private static readonly Dictionary<string, int> _confirmGen = new(StringComparer.Ordinal);   // HOST: "id|o"/"id|d" -> save round of the last confirmation
        private static int _saveGen;
        private static float _nextSendAt;

        private static readonly Dictionary<string, string> _packedHere = new(StringComparer.Ordinal);   // origin: packages this loaded world dropped, by PackKey

        /// <summary>N6: the origin's memory of a dropped package is per move AND release round ("&lt;move id&gt;#&lt;attempt&gt;"): a resend
        /// of the same round gets the same package back (its answer was lost), a re-ask at a later move hour always packs afresh.
        /// A late package's return ("&lt;move&gt;|late&lt;round&gt;.&lt;n&gt;") maps to the move's own entry for that round.</summary>
        private static string PackKey(ContentsHandoverPayload p)
        {
            string id = p.HandoverId ?? ""; int k = id.LastIndexOf("|late", StringComparison.Ordinal);
            return (k >= 0 ? id.Substring(0, k) : id) + "#" + p.Attempt;
        }
        private static readonly HashSet<string> _appliedHere = new(StringComparer.Ordinal);            // destination: hand-overs this loaded world applied
        private static readonly Dictionary<string, float> _offeredAt = new(StringComparer.Ordinal);
        private static object? _world;

        // the game's own move, run by THIS mod on the destination: flags read by the three MovingServiceHelper patches
        private static bool _applying, _skipBill, _payReached, _noMoney;

        internal static string DevFault = "";    // DEV lever movefault: "release" | "drop" | "apply" ("" = none)
        internal static bool DevFrozen;          // set by a fired drop/apply fault: this game answers nothing until it restarts
        internal static int DevPackErrors;       // DEV lever movefault packerror: the next N release ROUNDS here fail to pack (F2 proof)
        private static string _devPackErrKey = "";
        private static readonly HashSet<string> _returnRefused = new HashSet<string>(StringComparer.Ordinal);   // N3: warned once per move + reason

        internal static string IdFor(string originKey, string destKey) => "mv|" + originKey + ">" + destKey;

        /// <summary>Fold F6: one hand-over per BOOKING - the routed booking's id (MergerServiceLayouts' record, read back with
        /// the save and re-linked at world-ready, so a crash replay asks under the same id); a booking with no record falls
        /// back to its drawn hour and company. A rebooking of the same pair is a new hand-over.</summary>
        internal static string IdOf(MovingServiceContract mc, string okey, string dkey)
        {
            string bid = ""; try { bid = MergerServiceLayouts.RecordOf(mc)?.BookingId ?? ""; } catch { }
            if (bid.Length == 0) { string co = ""; try { co = Key(mc.movingCompanyRegistration?.Address); } catch { } bid = "h" + mc.movingHour + "@" + co; }
            return IdFor(okey, dkey) + "|" + bid;
        }

        private static string OwnerField(string list) { foreach (var (n, f) in OwnedLists) if (n == list) return f; return ""; }

        /// <summary>A row of this game's own (not a tagged absence install, not a company display copy).</summary>
        private static bool OwnRow(object x) { try { return !MergerAbsence.IsTaggedInstall(x) && !MergerAbsence.IsDisplayInstall(x); } catch { return true; } }

        /// <summary>Fold F2: a real worker leaves the list AND the game's id lookup, as every other path does (MergerAbsence :2318).</summary>
        private static int RemoveStaff(GameInstance gi, HashSet<string> sids, List<EmployeeInstance>? removed = null)
        {
            int n = 0;
            for (int i = gi.EmployeeInstances.Count - 1; i >= 0; i--)
            {
                var e = gi.EmployeeInstances[i];
                if (e?.id == null || !sids.Contains(e.id) || MPRegisterSync.IsInjectedStaff(e.id)) continue;
                gi.EmployeeInstances.RemoveAt(i); n++; removed?.Add(e);
                try { if (EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(e.id, out var d) && ReferenceEquals(d, e)) EmployeeHelper.EmployeeInstancesDictionary.Remove(e.id); } catch { }
            }
            return n;
        }

        private static void EnsureWorld()
        {
            var gi = SaveGameManager.Current;
            if (ReferenceEquals(_world, gi)) return;
            _world = gi; _packedHere.Clear(); _appliedHere.Clear(); _offeredAt.Clear();
        }

        private static string Ser(object? o, Type t) => JsonConvert.SerializeObject(o, t, Formatting.None, GameJson);
        private static T? De<T>(string s) where T : class { try { return JsonConvert.DeserializeObject<T>(s, GameJson); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Handover] record unreadable ({typeof(T).Name}): {ex.Message}"); return null; } }
        private static string Key(Address? a) { try { return a == null ? "" : GameStateReader.AddressKey(a); } catch { return ""; } }
        private static bool InsideAt(string key)
        {
            var gi = SaveGameManager.Current; var a = MergerAbsence.AddressOfKey(key);
            return gi != null && a != null && gi.CurrentStreetName == a.streetName && gi.CurrentStreetNumber == a.streetNumber;   // MovingServiceContract.IsInsideBuilding
        }

        private static IList? ListByName(GameInstance gi, string name)
        {
            switch (name)
            {
                case "DeliveryContracts":          return gi.DeliveryContracts as IList;
                case "FurnitureDeliveryContracts": return gi.FurnitureDeliveryContracts as IList;
                case "FoodDeliveryContracts":      return gi.FoodDeliveryContracts as IList;
                case "importPartnerships":         return gi.importPartnerships as IList;
                case "logisticsManagerPlans":      return gi.logisticsManagerPlans as IList;
                case "pricingManagerPlans":        return gi.pricingManagerPlans as IList;
                case "hrManagerPlans":             return gi.hrManagerPlans as IList;
                case "headhunterPlans":            return gi.headhunterPlans as IList;
                default: return null;
            }
        }

        private static string AddrField(object entry, string field)
        {
            try { return Key(AccessTools.Field(entry.GetType(), field)?.GetValue(entry) as Address); } catch { return ""; }
        }

        private static Type ElemType(IList list) => list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : typeof(object);

        private static MovingServiceContract? ContractFor(string okey, string dkey)
        {
            var l = SaveGameManager.Current?.movingServiceContracts;
            if (l == null) return null;
            foreach (var c in l) if (c != null && Key(c.originMovingAddress) == okey && Key(c.destinationMovingAddress) == dkey) return c;
            return null;
        }

        private static void Send(ContentsHandoverPayload p)
        {
            if (MPServer.IsRunning) HostOnMessage(p, MPConfig.PlayerId);
            else MPClient.SendHub(MessageType.ContentsHandover, p);
        }

        // ════════════════════════ DESTINATION: the move hour ════════════════════════

        /// <summary>The hourly MovingServiceContract.DoMove (BusinessHelper.HandleMovingContracts :508-518) for a move whose
        /// ORIGIN another game runs: nothing of the origin is in this save, so the game's own move cannot run yet. Ask the
        /// host for the origin's contents and skip the native pass; the contract stays and the next hour asks again. The
        /// move itself runs, untouched, when the package arrives (OnDeliver).</summary>
        [HarmonyPatch(typeof(MovingServiceContract), nameof(MovingServiceContract.DoMove))]
        public static class Patch_DoMove_CrossGame
        {
            static bool Prefix(MovingServiceContract __instance)
            {
                if (_applying) return true;
                try
                {
                    if ((!MPServer.IsRunning && !MPClient.IsClientInWorld) || MergerFlip.FlippedCount == 0) return true;
                    string okey = Key(__instance.originMovingAddress), dkey = Key(__instance.destinationMovingAddress);
                    bool oSim = false, dSim = false;
                    try { oSim = MergerAbsence.SimulatesHere(okey); dSim = MergerAbsence.SimulatesHere(dkey); } catch { }
                    if (oSim != dSim)
                    {   // one of the two buildings' owners is away and this game only stands in for it: nobody may release its
                        // contents, and real staff/lists must not land in a stand-in's save (fold F5) - the game's own postpone,
                        // a day at a time, until the owner runs it again (a move of TWO of the absent owner's buildings - both
                        // stood in for here - stays the game's own, slice 1A)
                        var gi = SaveGameManager.Current;
                        if (gi != null) __instance.movingDay = gi.Day + 1;
                        Plugin.Logger.LogInfo($"[Handover] {IdFor(okey, dkey)}: {(oSim ? "the origin" : "the destination")} is stood in for its absent owner - postponed to day {__instance.movingDay}.");
                        return false;
                    }
                    if (!IsCrossGame(okey)) return true;
                    Offer(__instance, okey, dkey);
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Handover] move hour: {ex.Message}"); return true; }
            }
        }

        /// <summary>The origin is a merged partner's building that another game runs (not truly mine, not stood in for here).</summary>
        internal static bool IsCrossGame(string okey)
        {
            if (okey.Length == 0 || !MergerFlip.IsFlipped(okey)) return false;
            var oreg = GameStatePatcher.FindRegistration(okey);
            if (oreg == null || MergerFlip.TrulyMine(oreg)) return false;
            try { if (MergerAbsence.SimulatesHere(okey)) return false; } catch { }
            return true;
        }

        internal static void Offer(MovingServiceContract mc, string okey, string dkey)
        {
            EnsureWorld();
            string id = IdOf(mc, okey, dkey);
            if (_offeredAt.TryGetValue(id, out var at) && Time.unscaledTime - at < ResendSeconds) return;
            _offeredAt[id] = Time.unscaledTime;
            string co = ""; try { co = Key(mc.movingCompanyRegistration?.Address); } catch { }
            Send(new ContentsHandoverPayload { Action = "offer", HandoverId = id, Purpose = "move", OriginKey = okey, DestKey = dkey,
                                               CompanyKey = co, Transfer = mc.transferBizManSettings, DestId = MPConfig.PlayerId });
            Plugin.Logger.LogInfo($"[Handover] {id}: move hour on the destination's game - the origin's contents asked for (transfer={mc.transferBizManSettings}).");
        }

        // ════════════════════════ HOST ════════════════════════

        private static string ResolvePid(string pid, string stable)
        {
            if (!string.IsNullOrEmpty(stable) && stable == MPConfig.StableId) return MPConfig.PlayerId;
            string p = ""; try { p = MPServer.PidOf(stable); } catch { }
            return string.IsNullOrEmpty(p) ? (pid ?? "") : p;
        }

        private static ContentsHandoverPayload Leg(MpHandoverEntry e, string action) => new ContentsHandoverPayload
        {
            Action = action, HandoverId = e.HandoverId, Purpose = e.Purpose, OriginKey = e.OriginKey, DestKey = e.DestKey,
            CompanyKey = e.CompanyKey, Transfer = e.Transfer, OriginId = ResolvePid(e.OriginPid, e.OriginStable), DestId = ResolvePid(e.DestPid, e.DestStable),
            PackageJson = action == "deliver" || action == "return" ? e.PackageJson : "",   // the host's copy rides every delivery and return
            Attempt = e.Attempt,
        };

        private static void SendTo(string pid, ContentsHandoverPayload p, IReadOnlyList<string>? online = null)
        {
            if (pid == MPConfig.PlayerId) { OnMessage(p); return; }
            if (online != null && !online.Contains(pid)) return;
            MPServer.SendHubTo(pid, MessageType.ContentsHandover, p);
        }

        private static void Await(MpHandoverEntry e, bool origin) { lock (_lock) _confirmGen[e.HandoverId + (origin ? "|o" : "|d")] = int.MaxValue; }
        private static void Note(MpHandoverEntry e, bool origin) { lock (_lock) _confirmGen[e.HandoverId + (origin ? "|o" : "|d")] = _saveGen; }

        private static bool IsDone(MpHandoverEntry e) => (e.State == "closed" && e.OriginClosed && e.OriginSaved && (e.DestSaved || e.Purpose == "late"))
                                                        || (e.State == "cancelled" && e.DestSaved);   // F2 fold F3: the cancel is in the destination's save

        /// <summary>F2 fold F2: the origin has given its contents up (a package exists or existed). "offer", "held" (postponed after a
        /// release error), "cancel" / "cancelled" (cancelled before any release) never released anything.</summary>
        private static bool Released(MpHandoverEntry e) => e.State != "offer" && e.State != "held" && e.State != "cancel" && e.State != "cancelled";

        /// <summary>F2 fold F1 + N5: a drop that arrives when the record no longer wants it (postponed, cancelled, gone) - the
        /// origin gave its contents up: the host keeps the package in its own record (Purpose "late", State "returning", id
        /// "&lt;move&gt;|late&lt;attempt&gt;") and sends it home until the origin confirms the reinstall and its save covers that -
        /// re-sent by the tick and at the origin's world-ready, like every other leg.</summary>
        private static void SendHome(ContentsHandoverPayload p, string originPid, string why)
        {
            // N7: one record per DIFFERENT package - the same package answered twice (a lost answer's resend) finds its record;
            // another package of the same round gets the next number
            string pre = (p.HandoverId ?? "") + "|late" + p.Attempt + ".", pj = p.PackageJson ?? "";
            MpHandoverEntry? le; string lid;
            lock (_lock)
            {
                le = _list.Find(x => x.Purpose == "late" && x.HandoverId.StartsWith(pre, StringComparison.Ordinal) && x.PackageJson == pj);
                lid = le?.HandoverId ?? pre + (_list.Count(x => x.HandoverId.StartsWith(pre, StringComparison.Ordinal)) + 1);
            }
            if (le == null)
            {
                le = new MpHandoverEntry { HandoverId = lid, Purpose = "late", State = "returning", Outcome = "returned", OriginPid = originPid,
                                           OriginStable = MPServer.StableOf(originPid) ?? "", OriginKey = p.OriginKey ?? "", DestKey = p.DestKey ?? "",
                                           PackageJson = p.PackageJson ?? "", Attempt = p.Attempt };
                try { le.Day = SaveGameManager.Current?.Day ?? 0; } catch { }
                lock (_lock) _list.Add(le);
                MPHub.SaveLedger();
                Plugin.Logger.LogWarning($"[Handover] {p.HandoverId}: a drop arrived late ({why}) - the host holds its package as {lid} and sends it home to '{originPid}'.");
            }
            if (le.State != "returning") return;   // already reinstalled: the record waits for the origin's save
            Await(le, true); SendTo(originPid, LateReturn(le));
        }

        private static ContentsHandoverPayload LateReturn(MpHandoverEntry e) { var r = Leg(e, "return"); r.Reason = "late"; return r; }

        /// <summary>DESTINATION (SharedShopWorkTabs.ApplyRoutedCancel): its booker cancelled a move between two games - the host's
        /// record of a move not yet released goes (F2 fold F2). One already released finds no booking at delivery: the package goes home.</summary>
        internal static void NoteBookingGone(MovingServiceContract mc)
        {
            try
            {
                string okey = Key(mc.originMovingAddress), dkey = Key(mc.destinationMovingAddress);
                if (!IsCrossGame(okey)) return;
                Send(new ContentsHandoverPayload { Action = "gone", HandoverId = IdOf(mc, okey, dkey), OriginKey = okey, DestKey = dkey });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handover] booking gone: {ex.Message}"); }
        }

        /// <summary>HOST: the origin may release only while its OWN owner runs it in this session - offline, or covered by a
        /// stand-in for an absent owner, the move is postponed a day (the game's own postpone, movingDay = Day + 1).</summary>
        private static string OriginRunner(string okey)
        {
            string owner = MPServer.OwnerPidOf(okey), runner = MPServer.RouteTargetFor(okey);
            return owner.Length > 0 && runner == owner ? runner : "";
        }

        internal static void HostOnMessage(ContentsHandoverPayload p, string senderPid)
        {
            if (p == null || !MPServer.IsRunning) return;
            try
            {
                MpHandoverEntry? e;
                lock (_lock) e = _list.Find(x => x.HandoverId == p.HandoverId);
                switch (p.Action)
                {
                    case "offer":
                    {
                        if (MPServer.RouteTargetFor(p.DestKey ?? "") != senderPid)
                        { Plugin.Logger.LogWarning($"[Handover] offer {p.HandoverId} from '{senderPid}', who does not run '{p.DestKey}' - dropped."); return; }
                        if (MPServer.OwnerPidOf(p.DestKey ?? "") != senderPid)
                        {   // fold F5: a stand-in runs the destination - postponed a day, as for the origin
                            Plugin.Logger.LogInfo($"[Handover] {p.HandoverId}: the destination '{p.DestKey}' is run by a stand-in - postponed a day.");
                            SendTo(senderPid, new ContentsHandoverPayload { Action = "hold", HandoverId = p.HandoverId, OriginKey = p.OriginKey ?? "", DestKey = p.DestKey ?? "", Reason = "dest" });
                            return;
                        }
                        if (e != null)
                        {
                            if (e.State == "dropped") { e.Deferred = false; SendTo(senderPid, Leg(e, "deliver")); }
                            else if (e.State == "held")
                            {   // F2: the next move hour after one release error - ask the origin again (same record, count kept)
                                if (OriginRunner(e.OriginKey) != ResolvePid(e.OriginPid, e.OriginStable))
                                { SendTo(senderPid, new ContentsHandoverPayload { Action = "hold", HandoverId = e.HandoverId, OriginKey = e.OriginKey, DestKey = e.DestKey, Reason = "origin" }); return; }
                                e.State = "offer"; e.Attempt++; MPHub.SaveLedger(); _nextSendAt = 0f;
                                Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: move hour again after {e.ErrorHolds} release error(s) - release asked.");
                            }
                            else if (e.State == "cancel") SendTo(senderPid, new ContentsHandoverPayload { Action = "cancel", HandoverId = e.HandoverId, OriginKey = e.OriginKey, DestKey = e.DestKey, Reason = "error" });
                            else if (e.State == "cancelled")
                            {   // F2 fold F3: the destination's game came back without the cancel in its save - cancelled again, silently
                                var cx = new ContentsHandoverPayload { Action = "cancel", HandoverId = e.HandoverId, OriginKey = e.OriginKey, DestKey = e.DestKey, Reason = "error" }; cx.Committed = true; Await(e, false); SendTo(senderPid, cx);
                            }
                            else if (e.State == "applied" || (e.State == "closed" && e.Outcome == "moved")) { var d = Leg(e, "deliver"); d.SkipBill = true; d.PackageJson = e.PackageJson; SendTo(senderPid, d); }
                            else if (e.Outcome == "returned") SendTo(senderPid, Leg(e, "cancel"));
                            return;   // "offer": the release is re-sent by the tick
                        }
                        string runner = OriginRunner(p.OriginKey ?? "");
                        if (runner.Length == 0 || runner == senderPid)
                        {
                            Plugin.Logger.LogInfo($"[Handover] {p.HandoverId}: the origin '{p.OriginKey}' is not run by its own owner now (runner '{MPServer.RouteTargetFor(p.OriginKey ?? "")}') - postponed a day.");
                            SendTo(senderPid, new ContentsHandoverPayload { Action = "hold", HandoverId = p.HandoverId, OriginKey = p.OriginKey ?? "", DestKey = p.DestKey ?? "", Reason = "origin" });
                            return;
                        }
                        e = new MpHandoverEntry
                        {
                            HandoverId = p.HandoverId, Purpose = string.IsNullOrEmpty(p.Purpose) ? "move" : p.Purpose, OriginKey = p.OriginKey ?? "", DestKey = p.DestKey ?? "",
                            CompanyKey = p.CompanyKey ?? "", Transfer = p.Transfer, OriginPid = runner, OriginStable = MPServer.StableOf(runner) ?? "",
                            DestPid = senderPid, DestStable = MPServer.StableOf(senderPid) ?? "", Attempt = 1,
                        };
                        try { e.Day = SaveGameManager.Current?.Day ?? 0; } catch { }
                        lock (_lock) _list.Add(e);
                        MPHub.SaveLedger();
                        Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: OFFER recorded - '{e.OriginKey}' (run by {runner}) -> '{e.DestKey}' (run by {senderPid}), transfer={e.Transfer}; release asked.");
                        SendTo(runner, Leg(e, "release"));
                        return;
                    }
                }
                if (e == null)
                {   // F2 fold F1: a drop for a record that is gone (cancelled and retired, or expired) - the package goes home
                    if (p.Action == "dropped" && !string.IsNullOrEmpty(p.PackageJson)) SendHome(p, senderPid, "no record");
                    return;
                }
                string origin = ResolvePid(e.OriginPid, e.OriginStable), dest = ResolvePid(e.DestPid, e.DestStable);
                bool fromOrigin = p.Action == "dropped" || p.Action == "held" || p.Action == "returned" || p.Action == "closedok";
                if (senderPid != (fromOrigin ? origin : dest))
                { Plugin.Logger.LogWarning($"[Handover] '{p.Action}' for {e.HandoverId} from '{senderPid}', not the {(fromOrigin ? "origin" : "destination")} - dropped."); return; }
                Note(e, fromOrigin);
                switch (p.Action)
                {
                    case "held":
                        if (!Released(e) && e.State != "offer") { Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: a late 'held' ({p.Reason}) in state {e.State} - nothing to do."); return; }
                        if (e.State != "offer")
                        {
                            Plugin.Logger.LogWarning($"[Handover] {e.HandoverId}: the origin could not apply a replayed drop ({p.Reason}) - state {e.State}; its save may still hold the copy of '{e.OriginKey}'.");
                            return;
                        }
                        if (p.Reason == "error")
                        {   // F2 (decision 57 a): a release ERROR is counted with the record; the 2nd in a row cancels the move
                            e.ErrorHolds++;
                            if (e.ErrorHolds >= 2)
                            {
                                e.State = "cancel"; MPHub.SaveLedger();
                                Plugin.Logger.LogWarning($"[Handover] {e.HandoverId}: CANCELLED - the origin '{e.OriginKey}' could not be released {e.ErrorHolds} times in a row; nothing moved, nothing charged; the destination drops the booking and its booker is told.");
                                SendTo(dest, new ContentsHandoverPayload { Action = "cancel", HandoverId = e.HandoverId, OriginKey = e.OriginKey, DestKey = e.DestKey, Reason = "error" });
                                return;
                            }
                            e.State = "held"; try { e.HeldDay = SaveGameManager.Current?.Day ?? 0; } catch { } MPHub.SaveLedger();
                            Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: the origin held (error {e.ErrorHolds} of 2) - nothing moved; postponed a day.");
                            SendTo(dest, new ContentsHandoverPayload { Action = "hold", HandoverId = e.HandoverId, OriginKey = e.OriginKey, DestKey = e.DestKey, Reason = p.Reason });
                            return;
                        }
                        lock (_lock) _list.Remove(e);
                        MPHub.SaveLedger();
                        Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: the origin held ({p.Reason}) - nothing moved; postponed a day.");
                        SendTo(dest, new ContentsHandoverPayload { Action = "hold", HandoverId = e.HandoverId, OriginKey = e.OriginKey, DestKey = e.DestKey, Reason = p.Reason });
                        return;
                    case "dropped":
                        if (e.State != "offer")
                        {   // a replayed drop's answer: the package is held already - unless nothing was ever released (F2 fold F1)
                            if (!Released(e) && !string.IsNullOrEmpty(p.PackageJson)) SendHome(p, senderPid, $"state {e.State}");
                            return;
                        }
                        if (string.IsNullOrEmpty(p.PackageJson)) { Plugin.Logger.LogWarning($"[Handover] {e.HandoverId}: a first drop came with no package - not recorded."); return; }
                        e.PackageJson = p.PackageJson; e.State = "dropped"; e.ErrorHolds = 0; Await(e, true); Note(e, true);   // F2: a release resets the error count
                        MPHub.SaveLedger();
                        if (e.Transfer)
                        {   // fold F4: the host's stored paperwork of the origin's owner loses the moved business NOW, once - an
                            // absence hand-over filled from it before the origin's next publish would bring it back
                            int sp = MPServer.StripStoredPaperwork(e.OriginStable, new HashSet<string>(StringComparer.Ordinal) { e.OriginKey });
                            if (sp > 0) Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: {sp} part(s) of '{e.OriginKey}' taken out of the host's stored paperwork.");
                        }
                        Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: DROPPED by the origin - the host holds the package ({p.PackageJson.Length} chars); delivering.");
                        SendTo(dest, Leg(e, "deliver"));
                        return;
                    case "gone":
                        if (e.State == "dropped")
                        {   // N1: released but not delivered (possibly waiting for the player to leave) - delivered again now: the
                            // destination finds no booking ("nocontract") and the package goes home on the return leg
                            e.Deferred = false; _nextSendAt = 0f; MPHub.SaveLedger();
                            Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: its booker cancelled the move after the release - delivered again so the package goes home.");
                            return;
                        }
                        if (Released(e) || e.State == "cancelled") return;   // applied / returning: the move (or its return) completes
                        lock (_lock) _list.Remove(e);
                        MPHub.SaveLedger();
                        Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: its booker cancelled the move (state {e.State}, {e.ErrorHolds} release error(s)) - record removed.");
                        return;
                    case "deferred":
                        if (e.State == "dropped") { e.Deferred = true; Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: the destination's player is inside - re-delivered at its next move hour."); }
                        return;
                    case "applied":
                        if (e.State != "dropped" && e.State != "applied" && e.State != "closed") return;
                        bool first = e.State == "dropped";
                        if (first) { e.State = "applied"; e.Outcome = "moved"; }
                        if (p.Charged && !e.Charged)
                        {
                            e.Charged = true;
                            if (senderPid == MPConfig.PlayerId && p.HasMoney) MPServer.RecordCash(MPConfig.PlayerId, p.Money);   // a guest's was recorded on receipt (MPServer)
                            e.ChargeStamped = MPSaveCoordinator.StampSlotCash(senderPid == MPConfig.PlayerId ? MPConfig.StableId : MPServer.StableOf(senderPid));
                        }
                        Await(e, false); Note(e, false);
                        MPHub.SaveLedger();
                        _nextSendAt = 0f;
                        Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: APPLIED on the destination's game (charged={e.Charged}, stamped={e.ChargeStamped}){(first ? "" : " - a replay")}.");
                        if (!e.OriginClosed) SendTo(origin, Leg(e, "closed"));
                        return;
                    case "applyfail":
                        if (e.State == "cancel")
                        {   // F2: the destination dropped the booking - kept until ITS save covers that (fold F3): a restart before it
                            // restores the booking, and the next offer / world-ready replay cancels it again (silently)
                            e.State = "cancelled"; e.Outcome = "cancelled"; Await(e, false); Note(e, false);
                            MPHub.SaveLedger();
                            Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: the cancelled move's booking is gone on the destination ({p.Reason}) - kept until its save covers it.");
                            return;
                        }
                        if (e.State == "cancelled") { Note(e, false); return; }   // a replayed cancel answered: its save now covers it
                        if (e.State != "dropped") return;
                        e.State = "return"; e.Outcome = "returned"; e.Deferred = false; Note(e, false);
                        MPHub.SaveLedger();
                        _nextSendAt = 0f;
                        Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: the destination's game did not move ({p.Reason}) - nothing charged; the package goes home.");
                        { var r = Leg(e, "return"); r.PackageJson = e.PackageJson; SendTo(origin, r); }
                        return;
                    case "returned":
                    case "closedok":
                        if (p.Reason == "notrun")
                        {   // N3: the origin's game does not run that building now (a stand-in) - re-sent at its next world-ready, not every tick
                            if (!e.Deferred) { e.Deferred = true; MPHub.SaveLedger(); Plugin.Logger.LogWarning($"[Handover] {e.HandoverId}: the origin's game does not run '{e.OriginKey}' now - the package ({e.PackageJson.Length} chars) stays here until its next world-ready."); }
                            return;
                        }
                        if (p.Reason == "unrented" || p.Reason == "unreadable")
                            Plugin.Logger.LogWarning($"[Handover] {e.HandoverId}: the origin could not take its package back ({p.Reason}: '{e.OriginKey}' is no longer its building) - {p.Count} record(s) ({e.PackageJson.Length} chars) NOT reinstalled; the record closes.");
                        if (e.OriginClosed) return;
                        e.OriginClosed = true; e.State = "closed"; Await(e, true); Note(e, true);
                        if (IsDone(e)) lock (_lock) _list.Remove(e);
                        MPHub.SaveLedger();
                        Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: CLOSED by the origin ({p.Action}); kept until both saves cover it.");
                        return;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handover] host '{p.Action}' {p.HandoverId}: {ex.Message}"); }
        }

        /// <summary>HOST tick (main thread, MPOffers.HostTick), every 5 s: each leg the record has not had answered.</summary>
        internal static void HostTick()
        {
            if (!MPServer.IsRunning || Time.unscaledTime < _nextSendAt) return;
            _nextSendAt = Time.unscaledTime + ResendSeconds;
            try
            {
                List<MpHandoverEntry> list;
                lock (_lock) list = new List<MpHandoverEntry>(_list);
                if (list.Count == 0) return;
                var online = MPRestSync.AllPlayers();
                int today = 0; try { today = SaveGameManager.Current?.Day ?? 0; } catch { }
                foreach (var e in list)
                {
                    string origin = ResolvePid(e.OriginPid, e.OriginStable), dest = ResolvePid(e.DestPid, e.DestStable);
                    switch (e.State)
                    {
                        case "offer":
                            if (OriginRunner(e.OriginKey) != origin)
                            {   // the origin left before it answered: nothing moved - the move waits a day
                                lock (_lock) _list.Remove(e);
                                MPHub.SaveLedger();
                                Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: the origin is no longer run by its owner before it released - postponed a day.");
                                SendTo(dest, new ContentsHandoverPayload { Action = "hold", HandoverId = e.HandoverId, OriginKey = e.OriginKey, DestKey = e.DestKey, Reason = "origin" }, online);
                            }
                            else SendTo(origin, Leg(e, "release"), online);
                            break;
                        case "dropped": if (!e.Deferred) SendTo(dest, Leg(e, "deliver"), online); break;
                        case "applied": if (!e.OriginClosed) SendTo(origin, Leg(e, "closed"), online); break;
                        case "return":  if (!e.Deferred) { var r = Leg(e, "return"); r.PackageJson = e.PackageJson; SendTo(origin, r, online); } break;
                        case "returning": if (!e.Deferred) SendTo(origin, LateReturn(e), online); break;   // N5: a late drop's package, until reinstalled
                        case "cancel":  SendTo(dest, new ContentsHandoverPayload { Action = "cancel", HandoverId = e.HandoverId, OriginKey = e.OriginKey, DestKey = e.DestKey, Reason = "error" }, online); break;
                        case "held":
                            if (today > 0 && e.HeldDay > 0 && today > e.HeldDay + 2)
                            {   // F2 fold F2: no move hour came back for two days - the booking is gone (the game's own cancel)
                                lock (_lock) _list.Remove(e);
                                MPHub.SaveLedger();
                                Plugin.Logger.LogInfo($"[Handover] {e.HandoverId}: postponed since day {e.HeldDay} and never asked again - its booking is gone; record removed.");
                            }
                            break;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handover] host tick: {ex.Message}"); }
        }

        /// <summary>HOST: a party reached world-ready. Its save may predate legs it confirmed (a crash): the origin is sent its
        /// drop again (by the ids the host holds) and its close, or its return; the destination its delivery WITHOUT the
        /// bill (the record says charged) - until that party's save covers them.</summary>
        internal static void HostReplayTo(string pid)
        {
            try
            {
                List<MpHandoverEntry> list;
                lock (_lock) list = new List<MpHandoverEntry>(_list);
                int n = 0;
                foreach (var e in list)
                {
                    string origin = ResolvePid(e.OriginPid, e.OriginStable), dest = ResolvePid(e.DestPid, e.DestStable);
                    if (pid == origin && !e.OriginSaved && Released(e))
                    {
                        if (e.Outcome == "returned") { var r = e.Purpose == "late" ? LateReturn(e) : Leg(e, "return"); r.PackageJson = e.PackageJson; e.Deferred = false; Await(e, true); SendTo(pid, r); n++; }
                        else
                        {
                            var rel = Leg(e, "release"); rel.Committed = true; rel.PackageJson = e.PackageJson; Await(e, true); SendTo(pid, rel); n++;
                            if (e.Outcome == "moved") { SendTo(pid, Leg(e, "closed")); n++; }
                        }
                    }
                    if (pid == dest && !e.DestSaved)
                    {
                        if (e.Outcome == "moved") { var d = Leg(e, "deliver"); d.SkipBill = true; d.PackageJson = e.PackageJson; Await(e, false); SendTo(pid, d); n++; }
                        else if (e.Outcome == "returned") { Await(e, false); SendTo(pid, Leg(e, "cancel")); n++; }
                        else if (e.Outcome == "cancelled") { var cx = new ContentsHandoverPayload { Action = "cancel", HandoverId = e.HandoverId, OriginKey = e.OriginKey, DestKey = e.DestKey, Reason = "error" }; cx.Committed = true; Await(e, false); SendTo(pid, cx); n++; }
                    }
                }
                if (n > 0) Plugin.Logger.LogInfo($"[Handover] world-ready replay to '{pid}': {n} leg(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handover] replay to '{pid}': {ex.Message}"); }
        }

        /// <summary>HOST: a save round begins - the same round number T-50's ledger hands out (MPBuildingSale.OnSaveNow).</summary>
        internal static void OnSaveNow(int round) { lock (_lock) _saveGen = round; }

        /// <summary>HOST, before a party's save is merged into the manifest: mark what that save covers (the T-50 rule: a guest's
        /// upload covers a leg only when it answers a round that started after the leg was confirmed).</summary>
        internal static void OnPartySaved(string stableId, int round = 0)
        {
            if (!MPServer.IsRunning || string.IsNullOrEmpty(stableId)) return;
            bool own = stableId == MPConfig.StableId;
            var done = new List<string>();
            lock (_lock)
            {
                bool GenOk(string key) => own ? (!_confirmGen.TryGetValue(key, out var g) || g <= _saveGen)
                                              : (round > 0 && (!_confirmGen.TryGetValue(key, out var h) || h < round));
                foreach (var e in _list)
                {
                    bool isO = e.OriginStable == stableId || (own && ResolvePid(e.OriginPid, e.OriginStable) == MPConfig.PlayerId);
                    bool isD = e.DestStable == stableId || (own && ResolvePid(e.DestPid, e.DestStable) == MPConfig.PlayerId);
                    if (isO && !e.OriginSaved && e.OriginClosed && GenOk(e.HandoverId + "|o")) { e.OriginSaved = true; done.Add($"{e.HandoverId} origin"); }
                    if (isD && !e.DestSaved && e.Outcome.Length > 0 && GenOk(e.HandoverId + "|d")) { e.DestSaved = true; done.Add($"{e.HandoverId} destination"); }
                }
                int gone = _list.RemoveAll(IsDone);
                if (gone > 0) done.Add($"{gone} RETIRED");
            }
            if (done.Count > 0) Plugin.Logger.LogInfo($"[Handover] save of '{stableId}' covers: {string.Join(", ", done)}.");
        }

        internal static List<MpHandoverEntry> SnapshotHost() { lock (_lock) return _list.Select(x => JsonConvert.DeserializeObject<MpHandoverEntry>(JsonConvert.SerializeObject(x))!).ToList(); }

        internal static void Restore(MpManifest? m)
        {
            lock (_lock)
            {
                _list.Clear(); _confirmGen.Clear();
                if (m?.Handovers != null) _list.AddRange(m.Handovers.Where(x => x != null && !string.IsNullOrEmpty(x.HandoverId)));
            }
            if (_list.Count > 0) Plugin.Logger.LogInfo($"[Handover] restored {_list.Count} open hand-over(s) from the manifest.");
        }

        /// <summary>HOST (PaperworkSync.StorePaperwork): while the ORIGIN's save does not yet cover its drop, a publish from
        /// it must not bring the moved business's rows back into the host's stored paperwork. True = rows were taken out.</summary>
        internal static bool HostFilterPaperwork(BusinessPaperworkPayload p, string senderPid)
        {
            HashSet<string>? addrs = null;
            lock (_lock)
                foreach (var e in _list)
                    if (e.Transfer && !e.OriginSaved && e.Outcome != "returned" && Released(e) && ResolvePid(e.OriginPid, e.OriginStable) == senderPid)
                        (addrs ??= new HashSet<string>(StringComparer.Ordinal)).Add(e.OriginKey);
            if (addrs == null) return false;
            int n = PaperworkSync.StripAddresses(p, addrs);
            if (n > 0) Plugin.Logger.LogInfo($"[Handover] paperwork from '{senderPid}': {n} part(s) of moved business(es) not stored - its save does not cover the hand-over yet.");
            return n > 0;
        }

        // ════════════════════════ PARTIES ════════════════════════

        internal static void OnMessage(ContentsHandoverPayload p)
        {
            if (p == null) return;
            try
            {
                if (DevFrozen) { Plugin.Logger.LogInfo($"[Handover] DEV fault fired - '{p.Action}' for {p.HandoverId} ignored until this game restarts."); return; }
                var gi = SaveGameManager.Current;
                if (gi == null || !MPWorldReady.IsSettled) return;   // the host re-sends
                EnsureWorld();
                switch (p.Action)
                {
                    case "release": OriginRelease(p); return;
                    case "closed":  OriginClose(p); return;
                    case "return":  OriginReturn(p); return;
                    case "hold":    DestHold(p); return;
                    case "cancel":  DestCancel(p); return;
                    case "deliver":
                        if (MPSaveCoordinator.CashOverlayPending) return;   // the bill would be overwritten by the host's figure - re-sent
                        DestDeliver(p); return;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handover] '{p.Action}' {p.HandoverId}: {ex.Message}"); }
        }

        private static void Reply(ContentsHandoverPayload p, string action, Action<ContentsHandoverPayload>? fill = null)
        {
            var r = new ContentsHandoverPayload { Action = action, HandoverId = p.HandoverId, OriginKey = p.OriginKey, DestKey = p.DestKey, Attempt = p.Attempt };   // N6/N7: the round rides every answer
            fill?.Invoke(r);
            Send(r);
        }

        // ── ORIGIN ──

        /// <summary>ORIGIN: pack everything the game's move would take from here, from THIS game's own save, and give it up.
        /// Committed = a replay after this game restarted: the host holds the package; drop exactly its ids again.</summary>
        private static void OriginRelease(ContentsHandoverPayload p)
        {
            var gi = SaveGameManager.Current!;
            var reg = GameStatePatcher.FindRegistration(p.OriginKey ?? "");
            if (p.Committed)
            {
                var pk = DePkg(p.PackageJson ?? "");
                if (reg == null || pk == null)
                {   // fold F6: answered once - the host logs it; this game's save still holds the origin's copy
                    Plugin.Logger.LogWarning($"[Handover] {p.HandoverId}: replayed drop NOT applied at '{p.OriginKey}' ({(reg == null ? "no registration here" : "the package does not read")}).");
                    Reply(p, "held", r => r.Reason = "error");
                    return;
                }
                int n = DropPackage(gi, reg, pk, p.Transfer);
                Plugin.Logger.LogInfo($"[Handover] {p.HandoverId}: replayed drop at '{p.OriginKey}' - {n} record(s) were still here and are given up again.");
                Reply(p, "dropped");
                return;
            }
            if (_packedHere.TryGetValue(PackKey(p), out var had)) { Reply(p, "dropped", r => r.PackageJson = had); return; }   // the answer was lost: same package
            string why = reg == null ? "noreg" : !MergerFlip.TrulyMine(reg) ? "notrun" : InsideAt(p.OriginKey ?? "") ? "inside" : "";
            if (why.Length > 0) { Plugin.Logger.LogInfo($"[Handover] {p.HandoverId}: release held ({why})."); Reply(p, "held", r => r.Reason = why); return; }
            // A record this game cannot pack, or a drop that fails, is answered ONCE as "held" (the design's existing path: the host
            // drops the offer, the destination's game postpones a day - the game's own postpone), never left to the host's
            // 5-second retry. A drop that failed part-way puts back what it took first (by id), so nothing is lost.
            HandoverPackage pack; string json;
            try
            {
                string devKey = (p.HandoverId ?? "") + "#" + p.Attempt;   // T4: once per release ROUND (move hour), never per resend
                if (DevPackErrors > 0 && devKey != _devPackErrKey) { DevPackErrors--; _devPackErrKey = devKey; throw new InvalidOperationException("DEV fault packerror"); }
                pack = Pack(gi, reg!, p.OriginKey ?? "", p.Transfer); json = JsonConvert.SerializeObject(pack);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Handover] {p.HandoverId}: RELEASE FAILED - '{p.OriginKey}' could not be packed ({ex.GetType().Name}: {ex.Message}); nothing given up, held - the move is postponed a day.");
                Reply(p, "held", r => r.Reason = "error");
                return;
            }
            int dropped; var undo = new List<Action>();
            try { dropped = DropPackage(gi, reg!, pack, p.Transfer, undo); }
            catch (Exception ex)
            {
                int back = 0;   // fold F3/F4: exactly what the drop took, in reverse (the schedule last)
                for (int u = undo.Count - 1; u >= 0; u--) { try { undo[u](); back++; } catch (Exception ex2) { Plugin.Logger.LogWarning($"[Handover] {p.HandoverId}: put-back step after a failed drop: {ex2.Message}"); } }
                Plugin.Logger.LogWarning($"[Handover] {p.HandoverId}: RELEASE FAILED - the drop at '{p.OriginKey}' threw ({ex.GetType().Name}: {ex.Message}); {back} record(s) put back, held - the move is postponed a day.");
                Reply(p, "held", r => r.Reason = "error");
                return;
            }
            _packedHere[PackKey(p)] = json;
            try { SaveGameManager.MarkChange(); } catch { }
            try { PaperworkSync.MarkDirty(); } catch { }
            Plugin.Logger.LogInfo($"[Handover] {p.HandoverId}: RELEASED '{p.OriginKey}' - {pack.Items.Count} item(s), {pack.Vehicles.Count} vehicle(s), {pack.Slots.Count} slot(s)"
                                + (pack.Business != null ? $", business: {pack.Business.Staff.Count} staff, {pack.Business.Lists.Sum(x => x.Value.Count)} list entr(ies)" : "") + $"; {dropped} record(s) given up.");
            if (DevFault == "release") { DevFault = ""; Plugin.Logger.LogInfo($"[Handover] DEV fault release: the drop of {p.HandoverId} is applied but its answer is lost once."); return; }
            Reply(p, "dropped", r => r.PackageJson = json);
            if (DevFault == "drop") { DevFault = ""; DevFrozen = true; Plugin.Logger.LogInfo($"[Handover] DEV fault drop: {p.HandoverId} dropped and answered - this game now answers nothing (crash point)."); }
        }

        /// <summary>Fold F5: a packed record is read back once - one that does not is a release failure (held/error), never a
        /// record silently left at the origin while the move reports success.</summary>
        private static string SerChecked(object? o, Type t)
        {
            string s = Ser(o, t);
            if (o != null && JsonConvert.DeserializeObject(s, t, GameJson) == null) throw new InvalidOperationException($"a {t.Name} record does not read back");
            return s;
        }

        private static HandoverPackage Pack(GameInstance gi, BuildingRegistration reg, string okey, bool transfer)
        {
            var pk = new HandoverPackage();
            foreach (var it in reg.itemInstances.Values.ToList()) pk.Items.Add(SerChecked(it, typeof(BigAmbitions.Items.ItemInstance)));
            foreach (var v in gi.VehicleInstances) if (v != null && Key(v.Address) == okey) pk.Vehicles.Add(SerChecked(v, typeof(VehicleInstance)));
            if (reg is Warehouse w && w.vehicleSlots != null) foreach (var s in w.vehicleSlots) pk.Slots.Add(SerChecked(s, typeof(VehicleSlot)));
            if (!transfer || string.IsNullOrEmpty(reg.BusinessName)) return pk;   // BizManTransfer.Transfer :18-21
            var b = new HandoverBusiness();
            foreach (var f in BizFields)
            {
                var fi = AccessTools.Field(reg.GetType(), f);
                if (fi == null) { Plugin.Logger.LogWarning($"[Handover] registration field '{f}' not found - not carried."); continue; }
                b.Fields[f] = SerChecked(fi.GetValue(reg), fi.FieldType);
            }
            foreach (var e in gi.EmployeeInstances)
                if (e?.id != null && Key(e.assignedAddress) == okey && !MPRegisterSync.IsInjectedStaff(e.id) && !MPRegisterSync.IsSyntheticDuty(e.id)) b.Staff.Add(SerChecked(e, typeof(EmployeeInstance)));
            foreach (var (name, field) in OwnedLists)
            {
                var l = ListByName(gi, name); if (l == null) continue;
                var rows = new List<string>();
                foreach (var x in l) if (x != null && OwnRow(x) && AddrField(x, field) == okey) rows.Add(SerChecked(x, ElemType(l)));
                if (rows.Count > 0) b.Lists[name] = rows;
            }
            pk.Business = b;
            return pk;
        }

        /// <summary>ORIGIN: give up what the package names (by id / by identical record), as the game's own move does on its
        /// origin (MovingServiceHelper :295-307, :577-582): items leave the building and its shifts, vehicles leave, their
        /// slots are freed; transfer settings: the staff and the owned list entries go too. Returns the records removed.</summary>
        private static int DropPackage(GameInstance gi, BuildingRegistration reg, HandoverPackage pk, bool transfer, List<Action>? undo = null)
        {
            int n = 0;
            if (undo != null)
            {   // fold F3: RemoveFromWorkShifts edits the building's schedule (ItemHelper.cs:961-963) - put back first-taken-last
                var sf = AccessTools.Field(reg.GetType(), "scheduleDays");
                if (sf != null) { string sched = Ser(sf.GetValue(reg), sf.FieldType); undo.Add(() => sf.SetValue(reg, JsonConvert.DeserializeObject(sched, sf.FieldType, GameJson))); }
            }
            var ids = new HashSet<string>(pk.Items.Select(s => De<BigAmbitions.Items.ItemInstance>(s)?.id?.ToString() ?? "").Where(s => s.Length > 0));
            foreach (var it in reg.itemInstances.Values.ToList())
                if (ids.Contains(it.id?.ToString() ?? "")) { reg.RemoveItemInstanceFromBuilding(it); it.RemoveFromWorkShifts(reg.Address); n++; undo?.Add(() => reg.AddItemInstanceToBuilding(it)); }
            var vids = new HashSet<string>(pk.Vehicles.Select(s => De<VehicleInstance>(s)?.id ?? "").Where(s => s.Length > 0));
            foreach (var v in gi.VehicleInstances.ToList())
                if (v?.id != null && vids.Contains(v.id))
                {
                    // the slot is freed BEFORE Delete, so the game's "vehicle unassigned" notice (VehicleInstance.cs:259-283, shown
                    // only while a slot still names the vehicle) is not raised for a vehicle that is handed over
                    if (reg is Warehouse w)
                        foreach (var s in w.vehicleSlots)
                            if (s.vehicleInstanceId == v.id)
                            {
                                string? drv = s.employeeDriverId; var slot = s;
                                s.vehicleInstanceId = null; if (transfer) s.employeeDriverId = null;
                                undo?.Add(() => { slot.vehicleInstanceId = v.id; slot.employeeDriverId = drv; });
                            }
                    v.Delete(); n++;
                    undo?.Add(() => { if (!gi.VehicleInstances.Contains(v)) gi.VehicleInstances.Add(v); });
                }
            if (pk.Business == null) return n;
            var sids = new HashSet<string>(pk.Business.Staff.Select(s => De<EmployeeInstance>(s)?.id ?? "").Where(s => s.Length > 0));
            var gone = new List<EmployeeInstance>();
            n += RemoveStaff(gi, sids, gone);
            foreach (var e0 in gone)
            {
                var e = e0;
                undo?.Add(() => { if (!gi.EmployeeInstances.Contains(e)) gi.EmployeeInstances.Add(e); try { EmployeeHelper.EmployeeInstancesDictionary[e.id] = e; } catch { } });
            }
            // fold F3: an entry is the origin's by its OWNING address (none of these game types has an id, and daily fields
            // such as nextDeliveryDay differ across a crash) - every own entry of that list owned by the origin goes
            string okey = Key(reg.Address);
            foreach (var kv in pk.Business.Lists)
            {
                var l = ListByName(gi, kv.Key); string f = OwnerField(kv.Key); if (l == null || f.Length == 0) continue;
                for (int i = l.Count - 1; i >= 0; i--)
                    if (l[i] != null && OwnRow(l[i]!) && AddrField(l[i]!, f) == okey)
                    {
                        var x = l[i]!; int at = i; var list = l;
                        l.RemoveAt(i); n++;
                        undo?.Add(() => list.Insert(Math.Min(at, list.Count), x));   // fold F4: exactly the rows taken, at their places
                    }
            }
            return n;
        }

        /// <summary>ORIGIN: the destination's game moved. Transfer settings: what of THIS game still points at the origin is
        /// re-pointed to the destination by the game's own rules (BizManTransfer :102-187 - a partner's building is a valid
        /// target: CargoTransfer / ImportTransfer carry those legs), the slots reset (:205) and the old business is closed
        /// as the game closes it, which wipes its history (BusinessHelper.ShutdownBusiness, :45).</summary>
        private static void OriginClose(ContentsHandoverPayload p)
        {
            var gi = SaveGameManager.Current!;
            var reg = GameStatePatcher.FindRegistration(p.OriginKey ?? "");
            var dAddr = MergerAbsence.AddressOfKey(p.DestKey ?? "");
            if (p.Transfer && reg != null && dAddr != null)
            {
                string o = p.OriginKey ?? "", d = p.DestKey ?? "";
                int rp = 0;
                foreach (var pl in gi.logisticsManagerPlans)
                {
                    if (pl == null) continue;
                    if (Key(pl.targetAddress) == d) { pl.targetAddress = null; rp++; }
                    else if (Key(pl.targetAddress) == o) { pl.targetAddress = dAddr; rp++; }
                    if (pl.destinations != null) foreach (var ds in pl.destinations) if (ds != null && Key(ds.deliveryTargetAddress) == o) { ds.deliveryTargetAddress = dAddr; rp++; }
                }
                foreach (var ip in gi.importPartnerships)
                    if (ip?.products != null) foreach (var pr in ip.products) if (pr != null && Key(pr.assignedWarehouse) == o) { pr.assignedWarehouse = dAddr; rp++; }
                if (reg is Warehouse w) w.ResetVehicleSlots();
                if (!string.IsNullOrEmpty(reg.BusinessName)) BusinessHelper.ShutdownBusiness(reg);
                try { SaveGameManager.MarkChange(); } catch { }
                try { PaperworkSync.MarkDirty(); } catch { }
                Plugin.Logger.LogInfo($"[Handover] {p.HandoverId}: closed at the origin - {rp} reference(s) re-pointed to '{d}', the old business closed.");
            }
            _packedHere.Remove(PackKey(p));
            Reply(p, "closedok");
        }

        /// <summary>ORIGIN: the destination could not move (too little money / not rented) - everything comes back, by id.</summary>
        private static void OriginReturn(ContentsHandoverPayload p)
        {
            var gi = SaveGameManager.Current!;
            var reg = GameStatePatcher.FindRegistration(p.OriginKey ?? "");
            var pk = DePkg(p.PackageJson ?? "");
            bool rentedHere = false; try { rentedHere = reg != null && reg.RentedByPlayer; } catch { }
            string why = pk == null ? "unreadable" : !rentedHere ? "unrented" : !MergerFlip.TrulyMine(reg!) ? "notrun" : "";
            if (why.Length > 0)
            {   // N3: never silent - answered (the host stops re-sending) and warned once, naming the move and what is not reinstalled
                int cnt = pk == null ? 0 : pk.Items.Count + pk.Vehicles.Count + pk.Slots.Count;
                if (_returnRefused.Add((p.HandoverId ?? "") + "|" + why))
                    Plugin.Logger.LogWarning($"[Handover] {p.HandoverId}: a return to '{p.OriginKey}' is NOT reinstalled ({why}) - {(pk == null ? "unreadable package" : $"{pk.Items.Count} item(s), {pk.Vehicles.Count} vehicle(s), {pk.Slots.Count} slot(s)")}{(why == "notrun" ? "; the host keeps it until this game runs the building" : "")}.");
                Reply(p, "returned", r => { r.Reason = why; r.Count = cnt; });
                return;
            }
            int n = Install(gi, reg!, pk!, null);
            _packedHere.Remove(PackKey(p));   // N6: a late package's return clears the move's entry of that round too
            try { SaveGameManager.MarkChange(); } catch { }
            try { PaperworkSync.MarkDirty(); } catch { }
            Plugin.Logger.LogInfo($"[Handover] {p.HandoverId}: RETURNED to '{p.OriginKey}' - {n} record(s) back.");
            Reply(p, "returned", r => r.Reason = p.Reason ?? "");   // "late": a late drop's package, not the record's return leg
        }

        /// <summary>Put a package's records into this save at the origin building (absent ids only). Returns the records added.
        /// replaced = the destination's own copies of the origin's contents it took out first (restored if the move fails).</summary>
        private static int Install(GameInstance gi, BuildingRegistration reg, HandoverPackage pk, List<BigAmbitions.Items.ItemInstance>? replaced, List<(IList, object)>? addedRows = null)
        {
            int n = 0;
            if (replaced != null) foreach (var it in reg.itemInstances.Values.ToList()) { reg.RemoveItemInstanceFromBuilding(it); replaced.Add(it); }
            var have = new HashSet<string>(reg.itemInstances.Values.Select(x => x.id?.ToString() ?? ""));
            foreach (var s in pk.Items) { var it = De<BigAmbitions.Items.ItemInstance>(s); if (it != null && have.Add(it.id?.ToString() ?? "")) { reg.AddItemInstanceToBuilding(it); n++; } }
            foreach (var s in pk.Vehicles)
            {
                var v = De<VehicleInstance>(s);
                if (v?.id == null || gi.VehicleInstances.Exists(x => x?.id == v.id)) continue;
                gi.VehicleInstances.Add(v); n++;
            }
            if (reg is Warehouse w && pk.Slots.Count > 0)
            {
                var slots = pk.Slots.Select(s => De<VehicleSlot>(s)).Where(s => s != null).Select(s => s!).ToList();
                if (slots.Count == pk.Slots.Count) w.vehicleSlots = slots;
            }
            if (pk.Business == null) return n;
            foreach (var kv in pk.Business.Fields)
            {
                var fi = AccessTools.Field(reg.GetType(), kv.Key);
                if (fi == null) continue;
                try { fi.SetValue(reg, JsonConvert.DeserializeObject(kv.Value, fi.FieldType, GameJson)); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Handover] field '{kv.Key}': {ex.Message}"); }
            }
            foreach (var s in pk.Business.Staff)
            {
                var e = De<EmployeeInstance>(s);
                if (e?.id == null) continue;
                MPRegisterSync.ForgetInjectedForAdopt(e.id);   // this game's stand-in copy of that worker goes: the real record arrives
                if (gi.EmployeeInstances.Exists(x => x?.id == e.id)) continue;
                gi.EmployeeInstances.Add(e); n++;
                try { EmployeeHelper.EmployeeInstancesDictionary[e.id] = e; } catch { }   // fold F2: the game's id lookup (MergerAbsence :2352)
            }
            string okey = Key(reg.Address);
            foreach (var kv in pk.Business.Lists)
            {
                var l = ListByName(gi, kv.Key); string f = OwnerField(kv.Key); if (l == null || f.Length == 0) continue;
                // fold F3: this save already holds the origin's own entries of that list (an older save after a crash) - kept
                bool haveOwn = false; foreach (var x in l) if (x != null && OwnRow(x) && AddrField(x, f) == okey) { haveOwn = true; break; }
                if (haveOwn) continue;
                foreach (var s in kv.Value)
                {
                    var x = JsonConvert.DeserializeObject(s, ElemType(l), GameJson);
                    if (x != null) { l.Add(x); addedRows?.Add((l, x)); n++; }
                }
            }
            return n;
        }

        // ── DESTINATION ──

        private static void DestHold(ContentsHandoverPayload p)
        {
            var gi = SaveGameManager.Current!;
            var mc = ContractFor(p.OriginKey ?? "", p.DestKey ?? "");
            if (mc == null) return;
            if (mc.movingDay <= gi.Day) mc.movingDay = gi.Day + 1;   // the game's own postpone (MovingServiceContract.HandleIsInsideBuilding :58)
            try { SaveGameManager.MarkChange(); } catch { }
            Plugin.Logger.LogInfo($"[Handover] {p.HandoverId}: postponed to day {mc.movingDay} ({p.Reason}).");
        }

        private static void DestCancel(ContentsHandoverPayload p)
        {
            var mc = ContractFor(p.OriginKey ?? "", p.DestKey ?? "");
            if (p.Reason == "error")
            {   // F2 (decision 57 a): the move cannot be released - cancelled through the routed-booking cancel, its booker told
                if (mc != null) SharedShopWorkTabs.CancelMoveNotReleased(mc, p.HandoverId ?? "", tell: !p.Committed);   // a replay: the booker was told
                else Plugin.Logger.LogInfo($"[Handover] {p.HandoverId}: the cancelled move's contract was already gone here.");
                Reply(p, "applyfail", r => r.Reason = "cancelled");
                return;
            }
            if (mc != null) SaveGameManager.Current!.movingServiceContracts.Remove(mc);   // what the game's DoMove did on the failed run (:46)
            Plugin.Logger.LogInfo($"[Handover] {p.HandoverId}: the failed move's contract {(mc != null ? "removed" : "was already gone")}.");
            Reply(p, "applyfail", r => r.Reason = "cancelled");
        }

        /// <summary>DESTINATION: the origin's records arrive. They are put into THIS save at the origin address, then the
        /// game's own move runs untouched (DoMove: bill, destination resale, relocation, slots, unfit vehicles sold, transfer
        /// settings). SkipBill = the host's record says this game was already charged (a replay after a crash): the bill and
        /// a sold vehicle's payout are not booked again. Repeats: the package's ids already at the destination.</summary>
        /// <summary>True while the destination puts a package in at the ORIGIN address and the game's own move takes it out: that
        /// building is another game's, and these are the hand-over's own steps - never member edits. Every guest interior forward
        /// (InteriorSync.ForwardGuestInteriorEditDelta / ForwardGuestDesignerClose - additions, removals, moves) stays silent;
        /// afterwards this game's copy of the origin is re-synced from its runner. Address-tied: only the origin is silenced.</summary>
        private static string _quietOriginKey = "";
        internal static bool IsQuietOrigin(string addressKey) => _quietOriginKey.Length > 0 && addressKey == _quietOriginKey;

        private static void DestDeliver(ContentsHandoverPayload p)
        {
            try { DestDeliverCore(p); }
            finally
            {
                if (_quietOriginKey.Length > 0)
                {   // the origin's copy here no longer matches what its runner holds: forget the edit baseline (no stale diff is
                    // ever forwarded from it) and take the runner's next snapshot whole. Staging only happens when this game does
                    // NOT run the origin (checked at this delivery, DestDeliverCore), so the runner is always another game.
                    string okey = _quietOriginKey; _quietOriginKey = "";
                    try { GameStatePatcher.ForgetInteriorBaseline(okey); InteriorSync.NoteResyncOwed(okey, $"hand-over {p.HandoverId} applied here"); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Handover] {p.HandoverId}: origin re-sync: {ex.Message}"); }
                }
            }
        }

        private static void DestDeliverCore(ContentsHandoverPayload p)
        {
            var gi = SaveGameManager.Current!;
            string okey = p.OriginKey ?? "", dkey = p.DestKey ?? "", id = p.HandoverId ?? "";
            var pk = DePkg(p.PackageJson ?? "");
            var oreg = GameStatePatcher.FindRegistration(okey); var dreg = GameStatePatcher.FindRegistration(dkey);
            if (pk == null || oreg == null || dreg == null) { Plugin.Logger.LogWarning($"[Handover] {id}: delivery unreadable or a building unknown here."); return; }
            if (_appliedHere.Contains(id) || AlreadyApplied(gi, dreg, pk))
            {
                Plugin.Logger.LogInfo($"[Handover] {id}: already applied in this save - confirmed, nothing moved again.");
                Reply(p, "applied");
                return;
            }
            var mc = ContractFor(okey, dkey);
            if (!p.SkipBill && mc == null) { Plugin.Logger.LogWarning($"[Handover] {id}: no move booked here for '{okey}' -> '{dkey}' - the package goes home."); Reply(p, "applyfail", r => r.Reason = "nocontract"); return; }
            string runsHere = "";
            try { runsHere = MergerAbsence.SimulatesHere(okey) ? "stands in for its absent owner" : MergerFlip.TrulyMine(oreg) || InteriorSync.OwnsAddressLocally(okey) ? "runs it" : ""; } catch { runsHere = "unknown"; }
            if (runsHere.Length > 0)
            {   // review F1: staging clears this game's copy of the origin - allowed only for a copy of ANOTHER game's building.
                // Decided from the fact at THIS delivery (an owner may have left since the offer): the move waits, as for a
                // player inside - postponed a day the game's way, re-delivered at the next move hour (host: "deferred").
                if (mc != null && mc.movingDay <= gi.Day) { mc.movingDay = gi.Day + 1; try { SaveGameManager.MarkChange(); } catch { } }
                Plugin.Logger.LogInfo($"[Handover] {id}: this game {runsHere} - '{okey}' - so the delivery waits (postponed to day {mc?.movingDay ?? gi.Day + 1}); nothing staged.");
                Reply(p, "deferred");
                return;
            }
            bool rented; try { rented = oreg.RentedByPlayer && dreg.RentedByPlayer; } catch { rented = false; }
            if (InsideAt(okey) || InsideAt(dkey) || (!rented && !p.SkipBill))
            {
                if (mc != null) { _applying = true; try { mc.DoMove(); } finally { _applying = false; } }   // the game's own answer: postpone / cancel, with its own message
                Plugin.Logger.LogInfo($"[Handover] {id}: the game's own move did not start ({(rented ? "player inside" : "a building is not rented")}).");
                if (rented) Reply(p, "deferred"); else Reply(p, "applyfail", r => r.Reason = "notrented");
                return;
            }
            var replaced = new List<BigAmbitions.Items.ItemInstance>();
            var fieldsBefore = new Dictionary<string, string>();
            if (pk.Business != null) foreach (var f in pk.Business.Fields.Keys) { var fi = AccessTools.Field(oreg.GetType(), f); if (fi != null) fieldsBefore[f] = Ser(fi.GetValue(oreg), fi.FieldType); }
            var slotsBefore = oreg is Warehouse w0 ? w0.vehicleSlots : null;
            var addedRows = new List<(IList, object)>();
            _quietOriginKey = okey;   // from here the origin address is touched by the hand-over only (reset by DestDeliver)
            int added = Install(gi, oreg, pk, replaced, addedRows);
            _skipBill = p.SkipBill; _payReached = false; _noMoney = false; _applying = true;
            try
            {
                if (mc != null) mc.DoMove();
                else
                {
                    var coAddr = MergerAbsence.AddressOfKey(p.CompanyKey ?? "");
                    var coReg = coAddr == null ? null : BuildingHelper.GetBuildingRegistration(coAddr);
                    Buildings.BuildingTypes.Special.MovingCompany.MovingServiceHelper.SetMovingServiceData(oreg.Address, dreg.Address, coReg, p.Transfer);
                    Buildings.BuildingTypes.Special.MovingCompany.MovingServiceHelper.MoveBusiness();
                }
            }
            finally { _applying = false; }
            bool moved = _payReached && !_noMoney; bool skipped = _skipBill; _skipBill = false;
            if (!moved)
            {
                // too little money: the game moved nothing and dropped the contract (:46) - take the package out again
                foreach (var it in oreg.itemInstances.Values.ToList()) oreg.RemoveItemInstanceFromBuilding(it);
                foreach (var it in replaced) oreg.AddItemInstanceToBuilding(it);
                var vids = new HashSet<string>(pk.Vehicles.Select(s => De<VehicleInstance>(s)?.id ?? ""));
                gi.VehicleInstances.RemoveAll(v => v?.id != null && vids.Contains(v.id));
                if (slotsBefore != null && oreg is Warehouse w1) w1.vehicleSlots = slotsBefore;
                if (pk.Business != null)
                {
                    RemoveStaff(gi, new HashSet<string>(pk.Business.Staff.Select(s => De<EmployeeInstance>(s)?.id ?? "")));
                    foreach (var (l, x) in addedRows) l.Remove(x);   // fold F3: exactly the rows this run added
                    foreach (var kv in fieldsBefore) { var fi = AccessTools.Field(oreg.GetType(), kv.Key); try { fi?.SetValue(oreg, JsonConvert.DeserializeObject(kv.Value, fi.FieldType, GameJson)); } catch { } }
                }
                Plugin.Logger.LogInfo($"[Handover] {id}: the game's own move did not happen ({(_noMoney ? "too little money - the game's own message" : "the bill was never reached")}) - nothing charged; the package goes home.");
                Reply(p, "applyfail", r => r.Reason = _noMoney ? "money" : "notmoved");
                return;
            }
            _appliedHere.Add(id);
            try { SaveGameManager.MarkChange(); } catch { }
            try { PaperworkSync.MarkDirty(); } catch { }
            Plugin.Logger.LogInfo($"[Handover] {id}: MOVED on this game - {added} record(s) arrived at '{okey}' and the game's own move took them to '{dkey}'{(skipped ? " (replay: no bill, no sale payout)" : "")}; money now {gi.Money:F2}.");
            Reply(p, "applied", r => { r.Charged = !skipped; r.Money = gi.Money; r.HasMoney = true; });
            if (DevFault == "apply") { DevFault = ""; DevFrozen = true; Plugin.Logger.LogInfo($"[Handover] DEV fault apply: {id} applied and answered - this game now answers nothing (crash point)."); }
        }

        private static bool AlreadyApplied(GameInstance gi, BuildingRegistration dreg, HandoverPackage pk)
        {
            var have = new HashSet<string>(dreg.itemInstances.Values.Select(x => x.id?.ToString() ?? ""));
            foreach (var s in pk.Items) { var id = De<BigAmbitions.Items.ItemInstance>(s)?.id?.ToString(); if (!string.IsNullOrEmpty(id) && have.Contains(id!)) return true; }
            string dkey = Key(dreg.Address);
            foreach (var s in pk.Vehicles) { var vid = De<VehicleInstance>(s)?.id; if (vid != null && gi.VehicleInstances.Exists(v => v?.id == vid && Key(v.Address) == dkey)) return true; }
            return false;
        }

        // The three points of the game's own move this mod reads or (on a replay only) holds back.
        [HarmonyPatch(typeof(Buildings.BuildingTypes.Special.MovingCompany.MovingServiceHelper), "PayMove")]
        public static class Patch_PayMove_Once
        {
            static bool Prefix(IEnumerable<BigAmbitions.Items.ItemInstance> itemsInDestinationAddress, IEnumerable<VehicleInstance> vehiclesInDestinationAddress, ref float itemsSoldAmount, ref bool __result)
            {
                if (!_applying) return true;
                _payReached = true;
                if (DevFault == "nomoney" && !_skipBill)
                {   // DEV lever movefault nomoney: the game's own refusal (ChangeMoneySafe answers false, :249) - nothing charged
                    DevFault = ""; __result = false; itemsSoldAmount = 0f;
                    Plugin.Logger.LogInfo("[Handover] DEV fault nomoney: the move's bill is refused as with too little money.");
                    return false;
                }
                if (!_skipBill) return true;
                itemsSoldAmount = 0f;   // the figure the game's done message shows (:225-232); the money was taken on the first run
                foreach (var i in itemsInDestinationAddress) itemsSoldAmount += i.GetSellingPrice();
                foreach (var v in vehiclesInDestinationAddress) itemsSoldAmount += v.GetSellingPrice();
                __result = true;
                Plugin.Logger.LogInfo("[Handover] replay: the move's bill was taken on the first run - not booked again.");
                return false;
            }
        }

        [HarmonyPatch(typeof(Buildings.BuildingTypes.Special.MovingCompany.MovingServiceHelper), "SendNotEnoughMoneyMessage")]
        public static class Patch_NotEnoughMoney_Note
        {
            static void Prefix() { if (_applying) _noMoney = true; }
        }

        [HarmonyPatch(typeof(Buildings.BuildingTypes.Special.MovingCompany.MovingServiceHelper), "SellSingleVehicle")]
        public static class Patch_SellSingleVehicle_Once
        {
            static bool Prefix() => !(_applying && _skipBill);   // a replay: the unfit vehicle's sale was paid on the first run
        }

#if BAMP_DEV
        // ════════════════════════ DEV levers (TestDrive) ════════════════════════
        internal static string DevState()
        {
            var sb = new System.Text.StringBuilder("OK movestate");
            lock (_lock) foreach (var e in _list) sb.Append($" [{e.HandoverId} state={e.State} outcome={e.Outcome} charged={e.Charged} stamped={e.ChargeStamped} oclosed={e.OriginClosed} osaved={e.OriginSaved} dsaved={e.DestSaved} pkg={e.PackageJson.Length}]");
            sb.Append($" host={(MPServer.IsRunning ? _list.Count : -1)} packedHere={_packedHere.Count} appliedHere={_appliedHere.Count} fault='{DevFault}' frozen={DevFrozen} cancelShown={SharedShopWorkTabs.MoveCancelShown}");
            return sb.ToString();
        }

        internal static string DevFaultSet(string arg)
        {
            string a = (arg ?? "").Trim();
            if (a == "packerror") { DevPackErrors = 2; return "OK movefault 'packerror' x2"; }
            if (a != "drop" && a != "apply" && a != "release" && a != "nomoney" && a != "off") return "ERR usage: movefault drop|apply|release|nomoney|packerror|off";
            DevFault = a == "off" ? "" : a; if (a == "off") { DevFrozen = false; DevPackErrors = 0; }
            return $"OK movefault '{DevFault}'";
        }

        internal static string DevMoveNow()
        {
            var gi = SaveGameManager.Current; if (gi == null) return "ERR no game";
            int n = 0, held = 0;
            foreach (var c in gi.movingServiceContracts.ToList())
            {
                if (c == null) continue;
                string o = Key(c.originMovingAddress), d = Key(c.destinationMovingAddress);
                bool oS = false, dS = false; try { oS = MergerAbsence.SimulatesHere(o); dS = MergerAbsence.SimulatesHere(d); } catch { }
                if (oS != dS) { c.movingDay = gi.Day; c.movingHour = gi.Hour; held++; continue; }   // the next hourly pass postpones it (stand-in)
                if (!IsCrossGame(o)) continue;
                c.movingDay = gi.Day; c.movingHour = gi.Hour; _offeredAt.Clear();
                Offer(c, o, d); n++;
            }
            return $"OK movenow cross={n} standin={held}";
        }

        /// <summary>`movepick wh` = this game's own warehouse with the most slotted vehicles; `movepick shop` = its own business
        /// (not a warehouse) with the most real staff; `movepick dest &lt;originKey&gt;` = its own building of the origin's
        /// building type, one with no business first. For the rig, which reads building types nowhere else.</summary>
        internal static string DevPick(string arg)
        {
            var gi = SaveGameManager.Current; if (gi?.BuildingRegistrations == null) return "ERR no game";
            var a = (arg ?? "").Trim().Split(new[] { ' ' }, 2);
            string mode = a[0]; BuildingRegistration? best = null; int bestScore = int.MinValue;
            var oreg = mode == "dest" && a.Length > 1 ? GameStatePatcher.FindRegistration(a[1].Trim()) : null;
            if (mode == "dest" && oreg == null) return "ERR usage: movepick wh|shop|dest <originKey>";
            foreach (var r in gi.BuildingRegistrations)
            {
                if (r == null || !MergerFlip.TrulyMine(r)) continue;
                string k = Key(r.Address); int score;
                if (mode == "wh")
                {
                    if (!(r is Warehouse w)) continue;
                    int slotted = 0, all = 0;
                    foreach (var v in gi.VehicleInstances) if (v != null && Key(v.Address) == k) { all++; if (w.vehicleSlots.Exists(s => s.vehicleInstanceId == v.id)) slotted++; }
                    score = Math.Min(w.vehicleSlots.Count, 2) * 1000 + slotted * 100 + all;   // 2+ vehicle slots first (leg 1: two slotted)
                }
                else if (mode == "shop")
                {
                    if (r is Warehouse || string.IsNullOrEmpty(r.BusinessName) || r.businessTypeName == "ba:businesstype_headquarters"
                        || (r.retailPrices?.Count ?? 0) == 0) continue;   // a shop: it sells at prices (leg 2 moves prices and a logo)
                    int staff = gi.EmployeeInstances.Count(e => e?.id != null && Key(e.assignedAddress) == k && !MPRegisterSync.IsInjectedStaff(e.id) && !MPRegisterSync.IsSyntheticDuty(e.id));
                    int contracts = gi.DeliveryContracts?.Count(c => c != null && OwnRow(c) && Key(c.businessAddress) == k) ?? 0;
                    int targets = 0;
                    foreach (var pl in gi.logisticsManagerPlans)
                        if (pl?.destinations != null) foreach (var ds in pl.destinations) if (ds != null && Key(ds.deliveryTargetAddress) == k) targets++;
                    score = Math.Min(staff, 9) * 1000 + Math.Min(contracts, 9) * 100 + Math.Min(targets, 9) * 10 + (r.logoSettings != null ? 1 : 0);   // staff, contracts, plan targets, logo
                }
                else if (mode == "dest")
                {
                    if (ReferenceEquals(r, oreg) || r.GetBuildingType() != oreg!.GetBuildingType()) continue;
                    score = (string.IsNullOrEmpty(r.BusinessName) ? 1000 : 0) - r.itemInstances.Count;
                    if (r is Warehouse dw)
                    {   // a warehouse destination: one with NO vehicles first (nothing of its own to destroy), then the most free slots
                        bool none = !gi.VehicleInstances.Exists(v => v != null && Key(v.Address) == k);
                        score += (none ? 100000 : 0) + dw.vehicleSlots.Count(s => string.IsNullOrEmpty(s.vehicleInstanceId)) * 100;
                    }
                }
                else return "ERR usage: movepick wh|shop|dest <originKey>";
                if (score > bestScore || (score == bestScore && best != null && string.CompareOrdinal(k, Key(best.Address)) < 0)) { best = r; bestScore = score; }
            }
            string sl = best is Warehouse bw ? $" slots={bw.vehicleSlots.Count} free={bw.vehicleSlots.Count(s => string.IsNullOrEmpty(s.vehicleInstanceId))}" : "";
            return best == null ? $"ERR movepick {mode}: none of my buildings fits" : $"OK movepick {mode} key='{Key(best.Address)}' score={bestScore} type={best.GetBuildingType()}{sl}";
        }

        /// <summary>`moveprep &lt;key&gt;` (leg-1 PREMISE, on the warehouse's runner): until the warehouse holds 2 SLOTTED vehicles and
        /// 1 parked (unslotted) one, add copies of a vehicle already parked there (VehicleInstance.Clone - new id, the game's
        /// own copy), slotted through the game's VehicleSlot.AssignVehicle; every slotted vehicle without cargo gets 10 of a
        /// product this save already stocks. Reports what it found and made.</summary>
        internal static string DevPrep(string key)
        {
            var gi = SaveGameManager.Current; var reg = GameStatePatcher.FindRegistration(key);
            if (gi == null || !(reg is Warehouse w) || !MergerFlip.TrulyMine(reg)) return "ERR moveprep: not a warehouse of mine";
            string k = Key(reg.Address);
            var here = gi.VehicleInstances.Where(v => v != null && Key(v.Address) == k).ToList();
            bool Slotted(VehicleInstance v) => w.vehicleSlots.Exists(s => s.vehicleInstanceId == v.id);
            var tpl = here.FirstOrDefault(Slotted) ?? here.FirstOrDefault();
            if (tpl == null) return "ERR moveprep: no vehicle parked there to copy";
            string cName = ""; float cPrice = 0f;
            foreach (var r in gi.BuildingRegistrations)
            {
                if (r == null || cName.Length > 0) continue;
                foreach (var it in r.itemInstances.Values) { var c = it.cargoInstances?.FirstOrDefault(x => x != null && x.amount > 0); if (c != null) { cName = c.itemName; cPrice = c.pricePerUnit; break; } }
            }
            int made = 0;
            VehicleInstance Add()
            {
                var v = tpl.Clone(); v.SetStreetData(reg.Address.streetName, reg.Address.streetNumber); v.position = tpl.position; v.rotation = tpl.rotation;
                gi.VehicleInstances.Add(v); here.Add(v); made++; return v;
            }
            int wantSlotted = Math.Min(2, w.vehicleSlots.Count), wantParked = 3 - wantSlotted;   // 3 vehicles; a 1-slot warehouse: 1 slotted + 2 parked
            if (wantSlotted == 0) return "ERR moveprep: the warehouse has no vehicle slot";
            while (here.Count(Slotted) < wantSlotted)
            {
                var free = w.GetVehicleFreeSlot();
                if (free == null) return $"ERR moveprep: only {here.Count(Slotted)} slotted and no free slot";
                free.AssignVehicle(Add());
            }
            while (here.Count(v => !Slotted(v)) < wantParked) Add();
            int cargo = 0;
            foreach (var v in here.Where(Slotted))
            {
                if (v.cargoInstances.Sum(c => c?.amount ?? 0) == 0 && cName.Length > 0) v.cargoInstances.Add(new BigAmbitions.Items.CargoInstance(cName, 10, cPrice, true));
                cargo += v.cargoInstances.Sum(c => c?.amount ?? 0);
            }
            try { SaveGameManager.MarkChange(); } catch { }
            return $"OK moveprep '{k}' slots={w.vehicleSlots.Count} made={made} slotted={here.Count(Slotted)} parked={here.Count(v => !Slotted(v))} cargo={cargo} product='{cName}'";
        }

        internal static string DevPrepArg(string arg)
        {
            string a = (arg ?? "").Trim();
            return a.StartsWith("shop ", StringComparison.Ordinal) ? DevPrepShop(DevKey(a.Substring(5))) : DevPrep(DevKey(a));
        }

        /// <summary>`moveprep shop &lt;key&gt;` (leg-2 PREMISE, on the shop's runner): the shop must hold 2+ real staff, prices and a
        /// logo (refused otherwise - those are the fixture's); a delivery contract and a logistics-plan delivery to it are SET
        /// UP when missing, as copies of this save's own (a contract re-addressed to the shop; a destination of an own plan
        /// copied and re-targeted) - the game's own records, so its move handles them as any other.</summary>
        internal static string DevPrepShop(string key)
        {
            var gi = SaveGameManager.Current; var reg = GameStatePatcher.FindRegistration(key);
            if (gi == null || reg == null || !MergerFlip.TrulyMine(reg)) return "ERR moveprep shop: not a building of mine";
            string k = Key(reg.Address);
            int staff = gi.EmployeeInstances.Count(e => e?.id != null && Key(e.assignedAddress) == k && !MPRegisterSync.IsInjectedStaff(e.id) && !MPRegisterSync.IsSyntheticDuty(e.id));
            int prices = reg.retailPrices?.Count ?? 0; bool logo = reg.logoSettings != null;
            if (staff < 2 || prices == 0 || !logo) return $"ERR moveprep shop: staff={staff} prices={prices} logo={logo} - the fixture's shop lacks them";
            string made = "";
            int contracts = gi.DeliveryContracts.Count(c => c != null && OwnRow(c) && Key(c.businessAddress) == k);
            if (contracts == 0)
            {
                var tpl = gi.DeliveryContracts.FirstOrDefault(c => c != null && OwnRow(c));
                if (tpl == null) return "ERR moveprep shop: this save has no delivery contract to copy";
                var c = De<DeliveryContract>(Ser(tpl, tpl.GetType()));
                if (c == null) return "ERR moveprep shop: contract copy unreadable";
                c.businessAddress = reg.Address; gi.DeliveryContracts.Add(c); contracts++; made += " contract";
            }
            int targets = 0;
            foreach (var pl in gi.logisticsManagerPlans)
                if (pl?.destinations != null && OwnRow(pl)) foreach (var ds in pl.destinations) if (ds != null && Key(ds.deliveryTargetAddress) == k) targets++;
            if (targets == 0)
            {
                var plan = gi.logisticsManagerPlans.FirstOrDefault(p => p?.destinations != null && p.destinations.Count > 0 && OwnRow(p) && Key(p.headquartersAddress) != k);
                if (plan == null) return "ERR moveprep shop: this save has no logistics plan with a destination to copy";
                var src = plan.destinations[0];
                var d = JsonConvert.DeserializeObject(Ser(src, src.GetType()), src.GetType(), GameJson);
                if (d == null) return "ERR moveprep shop: destination copy unreadable";
                AccessTools.Field(d.GetType(), "deliveryTargetAddress")?.SetValue(d, reg.Address);
                ((IList)plan.destinations).Add(d); targets++; made += " plantarget";
            }
            try { SaveGameManager.MarkChange(); } catch { }
            try { PaperworkSync.MarkDirty(); } catch { }
            return $"OK moveprep shop '{k}' staff={staff} contracts={contracts} planTargets={targets} prices={prices} logo={logo} made=[{made.Trim()}]";
        }

        /// <summary>`moveclear &lt;key&gt;` (leg-1 PREMISE, on the destination's runner): the destination warehouse holds none of its
        /// own vehicles - each is sold through the game's own sale (MovingServiceHelper.SellSingleVehicle) and deleted, its slot freed.</summary>
        internal static string DevClear(string key)
        {
            var gi = SaveGameManager.Current; var reg = GameStatePatcher.FindRegistration(key);
            if (gi == null || !(reg is Warehouse w) || !MergerFlip.TrulyMine(reg)) return "ERR moveclear: not a warehouse of mine";
            string k = Key(reg.Address); int sold = 0;
            foreach (var v in gi.VehicleInstances.Where(v => v != null && Key(v.Address) == k).ToList())
            {
                Traverse.Create(typeof(Buildings.BuildingTypes.Special.MovingCompany.MovingServiceHelper)).Method("SellSingleVehicle", v).GetValue();
                w.ClearSlotAssignments(v.id);
                v.Delete(); sold++;
            }
            try { SaveGameManager.MarkChange(); } catch { }
            int n = gi.VehicleInstances.Count(v => v != null && Key(v.Address) == k);
            return $"OK moveclear '{k}' sold={sold} n={n} slots={w.vehicleSlots.Count} free={w.vehicleSlots.Count(s => string.IsNullOrEmpty(s.vehicleInstanceId))}";
        }

        internal static string DevItemsAt(string key)
        {
            var reg = GameStatePatcher.FindRegistration(key); if (reg == null) return "ERR unknown building";
            int cargo = 0; var ids = new List<string>();
            foreach (var it in reg.itemInstances.Values) { ids.Add(it.id?.ToString() ?? ""); try { foreach (var c in it.cargoInstances) cargo += c.amount; } catch { } }
            ids.Sort(StringComparer.Ordinal);
            return $"OK itemsat '{key}' items={ids.Count} cargo={cargo} ids={string.Join(",", ids)}";
        }

        internal static string DevVehList(string key)
        {
            var gi = SaveGameManager.Current; if (gi == null) return "ERR no game";
            var reg = GameStatePatcher.FindRegistration(key);
            var rows = new List<string>();
            foreach (var v in gi.VehicleInstances)
            {
                if (v == null || Key(v.Address) != key) continue;
                int slot = -1; string drv = "";
                if (reg is Warehouse w) for (int i = 0; i < w.vehicleSlots.Count; i++) if (w.vehicleSlots[i].vehicleInstanceId == v.id) { slot = i; drv = w.vehicleSlots[i].employeeDriverId ?? ""; }
                int cargo = 0; try { foreach (var c in v.cargoInstances) cargo += c.amount; } catch { }
                rows.Add($"{v.id}|{v.vehicleTypeName}|cargo={cargo}|slot={slot}|driver={drv}");
            }
            rows.Sort(StringComparer.Ordinal);
            int nSlots = reg is Warehouse ww ? ww.vehicleSlots.Count : 0, nFree = reg is Warehouse wf ? wf.vehicleSlots.Count(s => string.IsNullOrEmpty(s.vehicleInstanceId)) : 0;
            int nSlotted = rows.Count(r => !r.Contains("|slot=-1|"));
            return $"OK vehlist '{key}' n={rows.Count} slotted={nSlotted} slots={nSlots} free={nFree} [{string.Join(" ", rows)}]";
        }

        /// <summary>`movecalc min &lt;a&gt; &lt;b&gt;`: the rig's arithmetic for a game rule's expected count (MovingServiceHelper
        /// .RelocateVehicles :577-603 - each slotted origin vehicle takes a free destination slot while one is left).</summary>
        internal static string DevCalc(string arg)
        {
            var a = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length != 3 || a[0] != "min" || !int.TryParse(a[1], out int x) || !int.TryParse(a[2], out int y)) return "ERR usage: movecalc min <a> <b>";
            return $"OK movecalc value={Math.Min(x, y)}";
        }

        internal static string DevBizAt(string key)
        {
            var reg = GameStatePatcher.FindRegistration(key); if (reg == null) return "ERR unknown building";
            int open = 0; try { open = reg.scheduleDays?.Count(d => d.isOpen) ?? 0; } catch { }
            return $"OK bizat '{key}' name='{reg.BusinessName}' type={reg.businessTypeName} logo={(reg.logoSettings != null)} prices={reg.retailPrices?.Count ?? 0} openDays={open} uniforms={reg.uniformsBySkill?.Count ?? 0} ads={reg.marketingCampaigns?.Count ?? 0}";
        }

        internal static string DevStaffAt(string key)
        {
            var gi = SaveGameManager.Current; if (gi == null) return "ERR no game";
            var rows = new List<string>();
            // a BAMP_DUTY_ record is the mod's on-duty display stand-in for a PARTNER's staffed shop (MPRegisterSync :2276-2281:
            // never anyone's staff, stripped from every save :2023) - counted apart, never as real
            foreach (var e in gi.EmployeeInstances)
                if (e?.id != null && Key(e.assignedAddress) == key)
                    rows.Add(e.id + (MPRegisterSync.IsSyntheticDuty(e.id) ? "|duty" : MPRegisterSync.IsInjectedStaff(e.id) ? "|standin" : "|real"));
            rows.Sort(StringComparer.Ordinal);
            int real = rows.Count(r => r.EndsWith("|real", StringComparison.Ordinal)), duty = rows.Count(r => r.EndsWith("|duty", StringComparison.Ordinal));
            int indict = 0;   // fold F2: real workers the game's own id lookup finds (shifts, vehicles, salary all read it)
            foreach (var e in gi.EmployeeInstances)
                if (e?.id != null && Key(e.assignedAddress) == key && !MPRegisterSync.IsInjectedStaff(e.id) && !MPRegisterSync.IsSyntheticDuty(e.id)
                    && EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(e.id, out var d) && ReferenceEquals(d, e)) indict++;
            return $"OK staffat '{key}' real={real} standin={rows.Count - real - duty} duty={duty} indict={indict} [{string.Join(" ", rows)}]";
        }

        private static string DevKey(string a)
        {
            var reg = GameStatePatcher.FindRegistration((a ?? "").Trim());
            return reg == null ? (a ?? "").Trim() : Key(reg.Address);
        }

        /// <summary>`listsat &lt;num&gt; &lt;street&gt;` - this game's OWN rows (no absence install, no company display copy) owned by the
        /// building per list, and its own plans' deliveries to it. `listsat &lt;num&gt; &lt;street&gt; sum &lt;8 counts A&gt; &lt;8 counts B&gt;`:
        /// do the own rows now equal A + B per list (two captured readouts)? match=True|False.</summary>
        internal static string DevListsArg(string arg)
        {
            var a = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length < 2) return "ERR usage: listsat <num> <street> [sum <counts A> <counts B>]";
            string key = DevKey(a[0] + " " + a[1]);
            string now = DevListsAt(key);
            if (a.Length == 2) return now;
            if (a[2] != "sum") return "ERR usage: listsat <num> <street> [sum <counts A> <counts B>]";
            var want = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 3; i < a.Length; i++)
            {
                var kv = a[i].Split('=');
                if (kv.Length != 2 || !int.TryParse(kv[1], out int v) || kv[0] == "planTargets") continue;
                want[kv[0]] = (want.TryGetValue(kv[0], out var h) ? h : 0) + v;
            }
            bool match = want.Count == OwnedLists.Length;
            foreach (var tok in now.Split(' '))
            {
                var kv = tok.Split('=');
                if (kv.Length == 2 && want.TryGetValue(kv[0], out var w) && (!int.TryParse(kv[1], out int v) || v != w)) match = false;
            }
            return $"OK listsat '{key}' sum match={match} expected=[{string.Join(" ", want.Select(x => x.Key + "=" + x.Value))}] now=[{now}]";
        }

        internal static string DevListsAt(string key)
        {
            var gi = SaveGameManager.Current; if (gi == null) return "ERR no game";
            var parts = new List<string>();
            foreach (var (name, field) in OwnedLists)
            {
                var l = ListByName(gi, name); if (l == null) continue;
                int own = 0; foreach (var x in l) if (x != null && OwnRow(x) && AddrField(x, field) == key) own++;
                parts.Add($"{name}={own}");
            }
            int tgt = 0;
            foreach (var pl in gi.logisticsManagerPlans)
            {
                if (pl == null || !OwnRow(pl)) continue;
                if (Key(pl.targetAddress) == key) tgt++;
                if (pl.destinations != null) foreach (var ds in pl.destinations) if (ds != null && Key(ds.deliveryTargetAddress) == key) tgt++;
            }
            parts.Add($"planTargets={tgt}");
            return $"OK listsat '{key}' {string.Join(" ", parts)}";
        }
#endif
    }
}
