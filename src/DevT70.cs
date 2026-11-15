#if BAMP_DEV
using System;
using System.Collections.Generic;
using System.Globalization;
using BigAmbitions.Tags;                         // TagRef.Vehicletag.isscooter
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>T-70 DEV levers (t-scooter; player report "if you rent scooter your friend can't use it", bundle 20261114-110317).
    /// Measurement only - nothing here changes mod behaviour.
    /// `rentscooter` rents an electric scooter at the nearest loaded rental point the way a player does: the game's own teleport to
    /// the point's nav-mesh stand spot (where the player's walk ends), its VehicleSpawnerController.Interact() (rent confirm,
    /// VehicleSpawnerController.cs:156-169), then the confirm key's own call HudConfirm.onConfirm (GameManager.cs:771-773) - the
    /// fee (ChangeMoneySafe, :165), CreateVehicle (:223) and SpawnVehicle, whose EnterVehicle one frame later MOUNTS the player (:188-196).
    /// `vehmove &lt;vid&gt; [seconds]` reads THIS machine's object for that vehicle (the own instance, else the mod's copy through
    /// VehicleManager.GhostTransform): rigidbodies kinematic / FreezeAll-constrained over total (the game's SetFreeze uses constraints,
    /// VehicleController.cs:526-536; the mod's copy uses isKinematic, VehicleManager.cs:1473-1483), NWH controllers enabled over total,
    /// the throttle the NWH input handler holds, controlledByPlayer. With seconds it starts a timed sample (start position now, end
    /// position taken by the ticker at t0+seconds); a later `vehmove &lt;vid&gt;` reports the distance.
    /// `throttle &lt;0..1&gt; [seconds]` feeds the layer a key press feeds: the key is read by InputSystemVehicleInputProvider.Throttle(),
    /// which InputProvider.CombinedInput sums with every other VehicleInputProviderBase in VehicleInputHandler.VC_Update (NWH,
    /// decompile update0916 VehicleInputHandler.cs:339). This registers one more such provider; its Throttle() returns the value while
    /// the local player still controls the vehicle it was started on.
    /// `ghostclick &lt;vid&gt;` runs the mod's single ghost-click entry (PassengerRide.RouteGhostClick) that a player's click on another
    /// player's vehicle lands in, reporting CanDriveGhost's reason first.
    /// Every PassengerHud toast is logged as "[TestDrive] toast: '...'" so a scenario sees what a player was shown.</summary>
    internal static class DevT70
    {
        private static DevT70Input? _drv;

        private static DevT70Input Driver()
        {
            if (_drv != null) return _drv;
            var go = new GameObject("BAMP_DevT70");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _drv = go.AddComponent<DevT70Input>();
            return _drv;
        }

        // ── rentscooter ───────────────────────────────────────────────────────────────────────────

        internal static string RentScooter(string arg)
        {
            if ((arg ?? "").Trim().Length > 0) return "ERR usage: rentscooter";
            var gi = SaveGameManager.Current;
            if (gi == null) return "ERR rentscooter: no game loaded";
            // With a vehicle active, Interact parks/returns it instead of renting (VehicleSpawnerController.cs:115-129).
            if (gi.ActiveVehicleId != null) return "ERR rentscooter: in a vehicle";
            if (HudConfirm.isOpen) return "ERR rentscooter: a confirm dialog is already open";
            Vector3 me;
            try { me = Helpers.PlayerHelper.GetPosition(); }
            catch (Exception ex) { return $"ERR rentscooter: {ex.Message}"; }
            VehicleSpawnerController? sp = null;
            float best = float.MaxValue;
            foreach (var s in UnityEngine.Object.FindObjectsOfType<VehicleSpawnerController>())
            {
                if (s == null) continue;
                bool scooter = false;
                try
                {
                    var vt = Vehicles.VehicleTypes.VehicleTypeHelper.GetVehicleType(s.vehicleType);
                    scooter = vt != null && vt.HasTag(TagRef.Vehicletag.isscooter);
                }
                catch { }
                if (!scooter) continue;
                float d = Vector3.Distance(me, s.transform.position);
                if (d < best) { best = d; sp = s; }
            }
            if (sp == null) return "ERR rentscooter: no scooter rental point loaded here";

            GameObject? probe = null;
            try
            {
                probe = new GameObject("BAMP_TpTarget");
                probe.transform.position = sp.GetClosestNavMeshTargetPosition(me);
                var pc = Helpers.PlayerHelper.PlayerController;
                probe.transform.rotation = pc != null ? pc.transform.rotation : Quaternion.identity;
                Helpers.PlayerHelper.Teleport(probe.transform);
            }
            catch (Exception ex) { return $"ERR rentscooter: teleport: {ex.Message}"; }
            finally { try { if (probe != null) UnityEngine.Object.Destroy(probe); } catch { } }

            var before = new HashSet<object>();
            foreach (var v in gi.VehicleInstances) if (v != null) before.Add(v);
            bool handled = false, confirmed = false;
            try
            {
                handled = sp.Interact();
                if (HudConfirm.isOpen) { HudConfirm.onConfirm?.Invoke(); confirmed = true; }
            }
            catch (Exception ex) { return $"ERR rentscooter: Interact threw {ex.GetType().Name} {ex.Message}"; }
            foreach (var v in gi.VehicleInstances)
            {
                if (v == null || before.Contains(v)) continue;
                string cost = sp.cost.ToString(CultureInfo.InvariantCulture);
                Plugin.Logger.LogInfo($"[TestDrive] rentscooter: rented '{v.id}' ({v.vehicleTypeName}) at '{sp.name}' {best:F0} m away, cost {cost}, confirm dialog {(confirmed ? "confirmed" : "skipped by the game")}; the game mounts the player next frame.");
                return $"OK rentscooter vid={v.id} type={v.vehicleTypeName} cost={cost} dist={best:F0} confirmed={confirmed} mounts=next-frame";
            }
            return $"ERR rentscooter: no vehicle created (interact={handled} confirmed={confirmed} dialogOpen={HudConfirm.isOpen}) - money short or the point refused";
        }

        // ── vehmove ───────────────────────────────────────────────────────────────────────────────

        private static string _mvVid = "";
        private static Transform? _mvRoot, _mvNwh;
        private static Vector3 _mvStart, _mvNwhStart;
        private static float _mvT0, _mvSecs, _mvTook, _mvDist, _mvNwhDist;
        private static int _mvState;   // 0 none, 1 running, 2 done, 3 lost

        private static Transform? FindVehicle(string vid, out string which)
        {
            which = "";
            var list = Helpers.VehicleHelper.AllPlayerVehicles;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var vc = list[i]; var inst = vc != null ? vc.vehicleInstance : null;
                    if (inst != null && inst.id == vid) { which = "own"; return vc!.transform; }
                }
            var g = VehicleManager.GhostTransform(vid);
            if (g != null) { which = "copy"; return g; }
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var vc = list[i]; var inst = vc != null ? vc.vehicleInstance : null;
                    if (inst != null && inst.id == "BAMP_" + vid) { which = "copy-registered"; return vc!.transform; }
                }
            return null;
        }

        internal static string VehMove(string arg)
        {
            var a = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float secs = 0f;
            if (a.Length < 1 || a.Length > 2
                || (a.Length == 2 && (!float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out secs) || secs <= 0f || secs > 60f)))
                return "ERR usage: vehmove <vehicleId> [seconds 0-60]";
            string vid = a[0];
            var root = FindVehicle(vid, out string which);
            if (root == null) return $"ERR vehmove: no vehicle '{vid}' here";
            var sb = new System.Text.StringBuilder();
            try
            {
                var rbs = root.GetComponentsInChildren<Rigidbody>(true);
                int kin = 0, frz = 0;
                foreach (var rb in rbs)
                {
                    if (rb == null) continue;
                    if (rb.isKinematic) kin++;
                    if (rb.constraints == RigidbodyConstraints.FreezeAll) frz++;
                }
                var nwhs = root.GetComponentsInChildren<NWH.VehiclePhysics2.VehicleController>(true);
                int nen = 0;
                NWH.VehiclePhysics2.VehicleController? nwh = null;
                foreach (var n in nwhs)
                {
                    if (n == null) continue;
                    if (nwh == null) nwh = n;
                    if (n.enabled) nen++;
                }
                var gvc = root.GetComponentInChildren<VehicleController>(true);
                var rrb = root.GetComponent<Rigidbody>();
                string nwhRbKin = nwh != null && nwh.vehicleRigidbody != null ? nwh.vehicleRigidbody.isKinematic.ToString() : "-";
                float thrIn = nwh != null && nwh.input != null ? nwh.input.Throttle : 0f;
                sb.Append($"OK vehmove vid={vid} which={which} name={root.name} kinematic={kin}/{rbs.Length} frozen={frz}/{rbs.Length} ")
                  .Append($"nwhEnabled={nen}/{nwhs.Length} nwhRbKinematic={nwhRbKin} throttleIn={thrIn.ToString("F2", CultureInfo.InvariantCulture)} ")
                  .Append($"ctl={(gvc != null && gvc.controlledByPlayer)} speed={(rrb != null ? rrb.velocity.magnitude : 0f).ToString("F2", CultureInfo.InvariantCulture)}");
                if (secs > 0f)
                {
                    _mvVid = vid; _mvRoot = root; _mvNwh = nwh != null ? nwh.transform : null;
                    _mvStart = root.position; _mvNwhStart = _mvNwh != null ? _mvNwh.position : Vector3.zero;
                    _mvT0 = Time.unscaledTime; _mvSecs = secs; _mvState = 1;
                    Driver();
                    sb.Append($" sample=started seconds={secs.ToString("F1", CultureInfo.InvariantCulture)}");
                }
                else if (_mvVid == vid) sb.Append(SampleText());
            }
            catch (Exception ex) { return $"ERR vehmove: {ex.GetType().Name} {ex.Message}"; }
            return sb.ToString();
        }

        private static string SampleText()
        {
            switch (_mvState)
            {
                case 1: return $" sample=running elapsed={(Time.unscaledTime - _mvT0).ToString("F1", CultureInfo.InvariantCulture)}/{_mvSecs.ToString("F1", CultureInfo.InvariantCulture)}";
                case 2: return $" sample=done seconds={_mvTook.ToString("F1", CultureInfo.InvariantCulture)} dist={_mvDist.ToString("F2", CultureInfo.InvariantCulture)} nwhDist={_mvNwhDist.ToString("F2", CultureInfo.InvariantCulture)}";
                case 3: return " sample=lost";
            }
            return "";
        }

        /// <summary>Ticker (DevT70Input.Update): takes the timed sample's end position at t0+seconds.</summary>
        internal static void Tick()
        {
            if (_mvState != 1) return;
            if (_mvRoot == null)
            {
                _mvState = 3;
                Plugin.Logger.LogInfo($"[TestDrive] vehmove: '{_mvVid}' sample lost - the vehicle object went away.");
                return;
            }
            float el = Time.unscaledTime - _mvT0;
            if (el < _mvSecs) return;
            _mvDist = Vector3.Distance(_mvStart, _mvRoot.position);
            _mvNwhDist = _mvNwh != null ? Vector3.Distance(_mvNwhStart, _mvNwh.position) : -1f;
            _mvTook = el;
            _mvState = 2;
            Plugin.Logger.LogInfo($"[TestDrive] vehmove: '{_mvVid}' moved {_mvDist:F2} m in {el:F1}s (NWH body {_mvNwhDist:F2} m).");
        }

        // ── throttle ──────────────────────────────────────────────────────────────────────────────

        private static float _thr, _thrUntil;
        private static VehicleController? _thrVc;
        private static string _thrVid = "";

        internal static string Throttle(string arg)
        {
            var a = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float v = 0f, s = 5f;
            if (a.Length < 1 || a.Length > 2
                || !float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0f || v > 1f
                || (a.Length == 2 && (!float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s) || s <= 0f || s > 30f)))
                return "ERR usage: throttle <0..1> [seconds 0-30]";
            VehicleController? cur = null;
            try
            {
                var list = Helpers.VehicleHelper.AllPlayerVehicles;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var vc = list[i];
                        if (vc != null && vc.controlledByPlayer) { cur = vc; break; }
                    }
            }
            catch (Exception ex) { return $"ERR throttle: {ex.Message}"; }
            if (cur == null) return "ERR throttle: not riding a vehicle";
            Driver();
            _thrVid = cur.vehicleInstance != null ? cur.vehicleInstance.id ?? "" : "";
            _thrVc = cur; _thr = v; _thrUntil = Time.unscaledTime + s;
            Plugin.Logger.LogInfo($"[TestDrive] throttle: {v:F2} for {s:F1}s on '{_thrVid}' through a DEV vehicle input provider.");
            return $"OK throttle vid={_thrVid} value={v.ToString("F2", CultureInfo.InvariantCulture)} seconds={s.ToString("F1", CultureInfo.InvariantCulture)} providers={NWH.Common.Input.InputProvider.Instances.Count}";
        }

        /// <summary>The DEV provider's Throttle(): the held value until expiry or until the player leaves that vehicle (the key release).</summary>
        internal static float ThrottleNow()
        {
            if (_thr <= 0f || _thrVc == null) return 0f;
            if (Time.unscaledTime >= _thrUntil || !_thrVc.controlledByPlayer)
            {
                _thr = 0f; _thrVc = null;
                Plugin.Logger.LogInfo($"[TestDrive] throttle released on '{_thrVid}'.");
                return 0f;
            }
            return _thr;
        }

        // ── ghostclick ────────────────────────────────────────────────────────────────────────────

        private static readonly System.Reflection.MethodInfo? _canDriveEx = AccessTools.Method(typeof(VehicleManager), "CanDriveGhostEx");

        internal static string GhostClick(string arg)
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
            string vid = (arg ?? "").Trim();
            if (vid.Length == 0 || vid.Contains(" ")) return "ERR usage: ghostclick <vehicleId>";
            if (VehicleManager.OwnerIdFor(vid) == MPConfig.PlayerId) return $"ERR ghostclick: '{vid}' is my own vehicle";
            if (VehicleManager.GhostTransform(vid) == null) return $"ERR ghostclick: no copy of '{vid}' here";
            string reason = "?";
            try
            {
                var args = new object?[] { vid, null };
                if (_canDriveEx != null) { _canDriveEx.Invoke(null, args); reason = args[1] as string ?? "?"; }
            }
            catch (Exception ex) { reason = "threw:" + ex.Message; }
            try { PassengerRide.RouteGhostClick(vid, "rig"); }
            catch (Exception ex) { return $"ERR ghostclick: {ex.Message}"; }
            return $"OK ghostclick vid={vid} reason={reason} routed";
        }
    }

    /// <summary>T-70: the DEV vehicle input provider (registered in InputProvider.Instances by the NWH base Awake) and the vehmove ticker.</summary>
    internal sealed class DevT70Input : NWH.VehiclePhysics2.Input.VehicleInputProviderBase
    {
        public override float Throttle() => DevT70.ThrottleNow();

        private void Update()
        {
            try { DevT70.Tick(); DevT70.ThrottleNow(); } catch { }
        }
    }

    /// <summary>T-70: log every PassengerHud toast (text only) so a scenario can read what a player was shown.</summary>
    [HarmonyPatch(typeof(PassengerHud), nameof(PassengerHud.Toast))]
    internal static class Patch_PassengerHud_Toast_DevLog
    {
        static void Postfix(string msg)
        {
            try { Plugin.Logger.LogInfo($"[TestDrive] toast: '{msg}'"); } catch { }
        }
    }
}
#endif
