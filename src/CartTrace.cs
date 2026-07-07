using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// DIAG [CartTrace] — the user-approved deductive instrument for the borrowed-hand-cart cluster
    /// (2026-07-07). Replaces the sampled probes with an EVENT-DRIVEN state recorder: every hand cart
    /// (borrowed ghost AND own — the own cart is the deduction baseline) has a full state vector
    /// diffed EVERY FRAME and logged the moment ANY field changes, stamped with the shared in-game
    /// clock so both machines' logs align without narration. Intent seams (grab / exit / every load
    /// path / building transitions) log the action WITH ITS CALLER, so "it stopped being pushed"
    /// resolves to a stack trace and "can't load" resolves to which path fired (or that none did).
    /// Analysis = align borrowed-cart timeline against own-cart baseline; the FIRST divergent field
    /// at the first divergent step is the bug seam. Whole file is removable DIAG.
    /// </summary>
    public static class CartTrace
    {
        // ── State watcher ─────────────────────────────────────────────────────
        private sealed class Snap { public string Vector = ""; public Vector3 Pos; public bool Seen; }
        private static readonly Dictionary<string, Snap> _last = new();
        private static readonly HashSet<string> _liveThisFrame = new();

        public static void Tick()
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected) return;
            try
            {
                WatchPlayer();   // the interaction layer's inputs (approved plan, 2026-07-07)
                _liveThisFrame.Clear();

                // Borrowed ghosts (the subject) — via the VehicleManager registry.
                foreach (var (id, go, ownerId, ownerUsing) in VehicleManager.CartGhosts())
                    Watch("GHOST(" + ownerId + ")", id, go, ownerUsing);

                // OWN carts (the baseline): any hand-cart VehicleController in the registered list
                // whose instance id is NOT a proxy, plus anything cart-like under the local player.
                var list = Helpers.VehicleHelper.AllPlayerVehicles;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var vc = list[i];
                        var inst = vc != null ? vc.vehicleInstance : null;
                        if (inst == null || string.IsNullOrEmpty(inst.id) || inst.id.StartsWith("BAMP_")) continue;
                        if (vc.gameObject == null || !VehicleManager.IsHandCartType(vc.gameObject.name)) continue;
                        Watch("OWN", inst.id, vc.gameObject, ownerUsing: false);
                    }

                // Carts that vanished from tracking entirely (destroyed/deregistered) — log the loss.
                List<string> gone = null;
                foreach (var kv in _last)
                    if (kv.Value.Seen && !_liveThisFrame.Contains(kv.Key)) (gone ??= new List<string>()).Add(kv.Key);
                if (gone != null)
                    foreach (var id in gone) { Emit(id, "UNTRACKED (object destroyed or deregistered)"); _last.Remove(id); }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CartTrace] tick: {ex.Message}"); }
        }

        private static void Watch(string kind, string id, GameObject go, bool ownerUsing)
        {
            if (go == null || string.IsNullOrEmpty(id)) return;
            _liveThisFrame.Add(id);

            var t = go.transform;
            string parent = t.parent != null ? t.parent.name : "(root)";
            string gparent = t.parent != null && t.parent.parent != null ? t.parent.parent.name : "-";
            var vc = go.GetComponentInChildren<VehicleController>(true);
            int rOn = 0, rAll = 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) { rAll++; if (r != null && r.enabled && r.gameObject.activeInHierarchy) rOn++; }
            int kinOn = 0, kinAll = 0;
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { kinAll++; if (rb != null && rb.isKinematic) kinOn++; }
            bool streaming = VehicleManager.DrivingRealVidNow == id;
            bool followed = VehicleManager.IsDrivenRemotely(id);

            string vec = $"{kind} p={parent}/{gparent} act={(go.activeSelf ? 1 : 0)} ctl={(vc != null && vc.controlledByPlayer ? 1 : 0)} " +
                         $"rnd={rOn}/{rAll} lay={go.layer} kin={kinOn}/{kinAll} stream={(streaming ? 1 : 0)} follow={(followed ? 1 : 0)} " +
                         $"ownerUse={(ownerUsing ? 1 : 0)} myBldg='{MPRegisterSync.CurrentShopAddress}'";

            if (!_last.TryGetValue(id, out var snap)) { snap = new Snap(); _last[id] = snap; }
            bool jumped = snap.Seen && Vector3.Distance(snap.Pos, t.position) > 3f;   // teleport-scale motion only
            if (vec != snap.Vector || jumped || !snap.Seen)
            {
                string posTxt = $"pos=({t.position.x:F0},{t.position.y:F1},{t.position.z:F0})" + (jumped ? $" JUMP {Vector3.Distance(snap.Pos, t.position):F0}m" : "");
                Emit(id, (snap.Seen ? "Δ " : "TRACK ") + vec + " " + posTxt);
                snap.Vector = vec; snap.Seen = true;
            }
            snap.Pos = t.position;
        }

        // ── The PLAYER's interaction-layer inputs (approved plan, 2026-07-07) ─────────────────────
        // Pickups route to the cart ONLY via: IsUsingVehicle (= ActiveVehicleId non-empty) AND
        // GetCurrentVehicle() resolving (for a proxy: only through the seeded VehiclesCache — proxies
        // are deliberately absent from the save's vehicle list). The cart UI reads the same inputs.
        // Diff-logging these four names the exact moment the interaction layer stops believing in the
        // cart — e.g. across a building entry.
        private static string _lastPlayerVec = "";

        private static void WatchPlayer()
        {
            string avid = "";
            try { avid = SaveGameManager.Current != null ? (SaveGameManager.Current.ActiveVehicleId ?? "") : ""; } catch { }
            bool usingVeh = false;
            try { usingVeh = Helpers.PlayerHelper.IsUsingVehicle; } catch { }
            string cur = "null";
            try { var v = Helpers.VehicleHelper.GetCurrentVehicle(); cur = v != null ? Short(v.id) : "null"; }
            catch (Exception ex) { cur = "threw:" + ex.GetType().Name; }
            // The SECOND resolution path (approved follow-up): GetCurrentVehicleBase resolves the
            // CONTROLLER through the scene — a proxy satisfied only via the instance cache would
            // diverge here, and several native consumers use this one.
            string curB = "null";
            try { var vb = Helpers.VehicleHelper.GetCurrentVehicleBase(); curB = vb != null && vb.vehicleInstance != null ? Short(vb.vehicleInstance.id) : (vb != null ? "noInst" : "null"); }
            catch (Exception ex) { curB = "threw:" + ex.GetType().Name; }
            bool cached = false;
            try
            {
                var d = AccessTools.Field(typeof(Helpers.VehicleHelper), "VehiclesCache")?.GetValue(null) as System.Collections.IDictionary;
                cached = d != null && !string.IsNullOrEmpty(avid) && d.Contains(avid);
            }
            catch { }

            string vec = $"avid={(string.IsNullOrEmpty(avid) ? "-" : Short(avid))} using={(usingVeh ? 1 : 0)} cur={cur} curB={curB} cached={(cached ? 1 : 0)}";
            if (vec != _lastPlayerVec)
            {
                Emit("PLAYER", (_lastPlayerVec.Length == 0 ? "TRACK " : "Δ ") + vec);
                _lastPlayerVec = vec;
            }
        }

        // ── Intent markers ────────────────────────────────────────────────────
        private static readonly Dictionary<string, float> _checkThrottle = new();
        internal static void NoteDriveCheck(string vid, bool ok, string reason)
        {
            // Hover CTAs poll this — log transitions/denials at most 1/s per cart.
            try
            {
                string key = vid + "|" + ok + "|" + reason;
                if (_checkThrottle.TryGetValue(key, out var until) && Time.unscaledTime < until) return;
                _checkThrottle[key] = Time.unscaledTime + 1f;
                Emit(vid, $"INTENT drive-check → {(ok ? "ALLOWED" : "denied: " + reason)}");
            }
            catch { }
        }

        internal static void Emit(string id, string msg)
        {
            string clock = "-";
            try { var gt = GameStateReader.GetGameTime(); clock = $"D{gt.day} {gt.hourOfDay:F2}h"; } catch { }
            Plugin.Logger.LogInfo($"[CartTrace] {Time.unscaledTime:F1} {clock} id={Short(id)} {msg}");
        }

        private static string Short(string id) => string.IsNullOrEmpty(id) ? "?" : (id.Length > 8 ? id.Substring(0, 8) : id);

        private static string Stack()
        {
            try
            {
                var lines = Environment.StackTrace.Split('\n');
                var sb = new StringBuilder();
                int kept = 0;
                foreach (var l in lines)
                {
                    var s = l.Trim();
                    if (s.StartsWith("at System.") || s.Contains("CartTrace") || s.Contains("Environment.get_StackTrace")) continue;
                    sb.Append(s.Replace("at ", "")).Append(" <- ");
                    if (++kept >= 10) break;
                }
                return sb.ToString();
            }
            catch { return "(stack unavailable)"; }
        }

        private static bool IsCartish(VehicleController vc)
            => vc != null && vc.gameObject != null && VehicleManager.IsHandCartType(vc.gameObject.name);

        private static string IdOf(VehicleController vc) => vc?.vehicleInstance?.id ?? "?";

        // Grab / exit — the possession seams. ExitVehicle carries the FULL CALLER STACK: an
        // involuntary "stopped pushing" resolves to whoever called this.
        [HarmonyPatch(typeof(VehicleController), nameof(VehicleController.DriveVehicle))]
        public static class Trace_DriveVehicle
        {
            static void Prefix(VehicleController __instance)
            { try { if (IsCartish(__instance)) Emit(IdOf(__instance), $"INTENT DriveVehicle (grab) ← {Stack()}"); } catch { } }
        }

        [HarmonyPatch(typeof(VehicleController), nameof(VehicleController.ExitVehicle))]
        public static class Trace_ExitVehicle
        {
            static void Prefix(VehicleController __instance)
            { try { if (IsCartish(__instance)) Emit(IdOf(__instance), $"INTENT ExitVehicle ← {Stack()}"); } catch { } }
        }

        // Load paths — every way cargo can enter/leave a cart. "Can't load" resolves to which of
        // these fired (and against which instance) or to their total absence.
        [HarmonyPatch(typeof(VehicleController), "AddHeldItemToStorage")]
        public static class Trace_AddHeld
        {
            static void Prefix(VehicleController __instance)
            { try { Emit(IdOf(__instance), $"INTENT AddHeldItemToStorage held='{Helpers.PlayerHelper.ItemInstanceInHands?.itemName ?? "none"}'"); } catch { } }
        }

        [HarmonyPatch(typeof(VehicleController), "AddHandTruckItemsToStorage")]
        public static class Trace_AddTruckItems
        {
            static void Prefix(VehicleController __instance)
            { try { Emit(IdOf(__instance), "INTENT AddHandTruckItemsToStorage (truck→this)"); } catch { } }
        }

        [HarmonyPatch(typeof(VehicleController), "MoveAndAddHandTruckItemsToStorage")]
        public static class Trace_MoveAddTruckItems
        {
            static void Prefix(VehicleController __instance)
            { try { Emit(IdOf(__instance), "INTENT MoveAndAddHandTruckItems (walk, truck→this)"); } catch { } }
        }

        [HarmonyPatch(typeof(VehicleInstance), nameof(VehicleInstance.TryToAddToCargo))]
        public static class Trace_TryAdd
        {
            static void Postfix(VehicleInstance __instance, bool __result)
            {
                try
                {
                    var tn = __instance?.vehicleTypeName?.ToString() ?? "";
                    if (!VehicleManager.IsHandCartType(tn) && !(__instance?.id ?? "").StartsWith("BAMP_")) return;
                    Emit(__instance.id, $"CARGO TryToAddToCargo → {(__result ? "OK" : "REFUSED")} ← {Stack()}");
                }
                catch { }
            }
        }

        // Building transitions — with what was in the player's hands at that instant.
        [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.EnterBuilding))]
        public static class Trace_EnterBuilding
        {
            static void Prefix(object buildingToEnter)
            { try { Emit("-", $"EVENT EnterBuilding '{buildingToEnter}' cartsUnderPlayer=[{UnderPlayer()}]"); } catch { } }
        }

        [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.ExitFromBuilding))]
        public static class Trace_ExitBuilding
        {
            static void Prefix()
            { try { Emit("-", $"EVENT ExitFromBuilding cartsUnderPlayer=[{UnderPlayer()}]"); } catch { } }
        }

        // ── Approved 3-part extension (2026-07-07): interaction-layer markers ─────────────────────

        // (1) The pickup router's EXACT conjuncts, sampled at the click. ItemController.Interact:509
        //     routes to the cart only when IsUsingVehicle && ItemInstance != null && alwaysinteractable;
        //     any false conjunct silently falls through to the HANDS path at :533. The per-frame PLAYER
        //     vector can't catch a same-frame transient — this can. Logs only while a cart is in play
        //     (avid set, using true, or a cart parented under the player) so ordinary clicks stay silent.
        [HarmonyPatch(typeof(ItemController), nameof(ItemController.Interact))]
        public static class Trace_Interact
        {
            static void Prefix(ItemController __instance)
            {
                try
                {
                    string avid = "";
                    try { avid = SaveGameManager.Current?.ActiveVehicleId ?? ""; } catch { }
                    bool usingVeh = false;
                    try { usingVeh = Helpers.PlayerHelper.IsUsingVehicle; } catch { }
                    string under = UnderPlayer();
                    if (string.IsNullOrEmpty(avid) && !usingVeh && string.IsNullOrEmpty(under)) return;

                    bool inst = false; try { inst = __instance.ItemInstance != null; } catch { }
                    bool tagAI = false, tagBox = false;
                    try
                    {
                        var it = __instance.Item;
                        if (it != null)
                        {
                            tagAI = it.HasTag(BigAmbitions.Tags.TagRef.Itemtag.alwaysinteractable);
                            tagBox = it.HasTag(BigAmbitions.Tags.TagRef.Itemtag.isbox);
                        }
                    }
                    catch { }
                    string cur = "null";
                    try { var v = Helpers.VehicleHelper.GetCurrentVehicle(); cur = v != null ? Short(v.id) : "null"; }
                    catch (Exception ex) { cur = "threw:" + ex.GetType().Name; }
                    string curB = "null";
                    try { var vb = Helpers.VehicleHelper.GetCurrentVehicleBase(); curB = vb != null && vb.vehicleInstance != null ? Short(vb.vehicleInstance.id) : (vb != null ? "noInst" : "null"); }
                    catch (Exception ex) { curB = "threw:" + ex.GetType().Name; }
                    string held = "none";
                    try { held = Helpers.PlayerHelper.ItemInstanceInHands?.itemName ?? "none"; } catch { }

                    Emit("PLAYER", $"CLICK Interact item='{__instance.itemName}' inst={(inst ? 1 : 0)} tagAI={(tagAI ? 1 : 0)} tagBox={(tagBox ? 1 : 0)} " +
                                   $"using={(usingVeh ? 1 : 0)} avid={(string.IsNullOrEmpty(avid) ? "-" : Short(avid))} cur={cur} curB={curB} held='{held}'");
                }
                catch { }
            }
        }

        //     The chosen route: TryToGrabItem IS the hands path (Interact:535 / :529 child fallthrough).
        //     If this fires right after a CLICK line whose conjuncts all read true, the cart branch ran
        //     and its TryToAddToCargo refused (cross-check the CARGO line); if the CLICK shows a false
        //     conjunct, the cart branch never ran at all.
        [HarmonyPatch(typeof(ItemController), nameof(ItemController.TryToGrabItem))]
        public static class Trace_TryGrab
        {
            static void Prefix(ItemController __instance)
            {
                try
                {
                    string avid = "";
                    try { avid = SaveGameManager.Current?.ActiveVehicleId ?? ""; } catch { }
                    if (string.IsNullOrEmpty(avid) && string.IsNullOrEmpty(UnderPlayer())) return;
                    Emit("PLAYER", $"ROUTE TryToGrabItem (hands path) item='{__instance.itemName}'");
                }
                catch { }
            }
        }

        // (3) EnterVehicle — the possession seam itself. VehicleController.EnterVehicle:263-282 EARLY-
        //     RETURNS (no layer 19, no avid, no ctl) when the player holds an item that the cart's
        //     TryToAddToCargo refuses — and HandTruck.EnterVehicle keeps parenting/IK REGARDLESS after
        //     base() returns, i.e. "looks pushed, isn't possessed". Prefix logs pre-state + caller;
        //     postfix logs what the native pass actually committed. Layer sampled on root AND first
        //     renderer child (:289 sets layer only on the cached renderers array).
        [HarmonyPatch(typeof(VehicleController), nameof(VehicleController.EnterVehicle))]
        public static class Trace_EnterVehicle
        {
            static void Prefix(VehicleController __instance)
            {
                try
                {
                    if (!IsCartish(__instance)) return;
                    Emit(IdOf(__instance), $"INTENT EnterVehicle pre: {LayerState(__instance)} held='{HeldName()}' ← {Stack()}");
                }
                catch { }
            }

            static void Postfix(VehicleController __instance)
            {
                try
                {
                    if (!IsCartish(__instance)) return;
                    string avid = "";
                    try { avid = SaveGameManager.Current?.ActiveVehicleId ?? ""; } catch { }
                    bool committed = avid == (__instance.vehicleInstance?.id ?? "?");
                    Emit(IdOf(__instance), $"EnterVehicle base DONE: {LayerState(__instance)} ctl={(__instance.controlledByPlayer ? 1 : 0)} " +
                                           $"avidSet={(committed ? 1 : 0)} held='{HeldName()}'{(committed ? "" : " ⚠ EARLY-RETURN (possession NOT committed)")}");
                }
                catch { }
            }
        }

        //     The HandTruck override finishes parenting/audio/IK after base() — this line marks the full
        //     override completing, so an early-returned base shows up as: "base DONE ⚠" then "override DONE".
        [HarmonyPatch(typeof(HandTruck), nameof(HandTruck.EnterVehicle))]
        public static class Trace_EnterVehicle_HandTruck
        {
            static void Postfix(HandTruck __instance)
            {
                try
                {
                    var t = __instance.transform;
                    Emit(IdOf(__instance), $"EnterVehicle override DONE: parent={(t.parent != null ? t.parent.name : "(root)")} {LayerState(__instance)}");
                }
                catch { }
            }
        }

        private static string HeldName()
        { try { return Helpers.PlayerHelper.ItemInstanceInHands?.itemName ?? "none"; } catch { return "?"; } }

        private static string LayerState(VehicleController vc)
        {
            try
            {
                int root = vc.gameObject.layer;
                int rnd = -1;
                var arr = AccessTools.Field(typeof(EntityController), "renderers")?.GetValue(vc) as Renderer[];
                if (arr != null && arr.Length > 0 && arr[0] != null) rnd = arr[0].gameObject.layer;
                return $"lay=root:{root}/rnd:{rnd}({(arr?.Length ?? -1)})";
            }
            catch { return "lay=?"; }
        }

        private static string UnderPlayer()
        {
            try
            {
                var pc = Helpers.PlayerHelper.PlayerController;
                if (pc == null) return "";
                var sb = new StringBuilder();
                foreach (var vc in pc.GetComponentsInChildren<VehicleController>(true))
                    if (vc?.vehicleInstance != null) sb.Append(Short(vc.vehicleInstance.id)).Append(' ');
                return sb.ToString().TrimEnd();
            }
            catch { return "?"; }
        }
    }
}
