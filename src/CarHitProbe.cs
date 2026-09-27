// PROBE-START: P-CARHIT
using System.Text;
using GleyTrafficSystem;   // VehicleComponent - Gley's own traffic car, which receives its own OnCollisionEnter
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>P-CARHIT (H-CARSTACK-1 / H-TRAFFIC-RAM-1 evidence; user direction 2026-09-27) - a LOG-ONLY probe for
    /// car-to-car PHYSICS CONTACTS at any force. Damage only lands on owned cars, so damage alone never shows a traffic
    /// car running into another traffic car; this listens to the contact events the game already receives:
    ///   - VehicleDeformationController.OnCollisionEnter (every player car - driven, parked, and materialised ghosts);
    ///   - GleyTrafficSystem.VehicleComponent.OnCollisionEnter (every Gley traffic car of THIS machine's simulation).
    /// Two kinematic bodies (a traffic replica against a car ghost) never raise a contact in PhysX, so such pairs are
    /// invisible here by construction. Both sides of a pair usually get the event: the pair is logged ONCE per 1 s.
    ///
    /// Each side is CLASSIFIED: driver (the car the local player is driving), parked-player (a player car nobody here
    /// drives), traffic (a Gley car of this machine), traffic-replica (TrafficSync's kinematic copy of another machine's
    /// car), car-ghost (VehicleManager's copy of another player's car), parked-ambient / parked-replica (ParkedVehicleSync's
    /// host-tracked cars / client copies), ghost-other (a mod ghost no registry names), other (any other rigidbody on the
    /// car's layer). A contact that does not involve the local driver prints one detail line (budget 200 per session);
    /// a contact that does is only COUNTED per class pair and summarised at most once per 60 s. Changes nothing: no
    /// behaviour, no wire, no on-screen text. MP sessions only. DEV lever `carhit` reads the counters.</summary>
    internal static class CarHitProbe
    {
        internal static bool Armed = true;   // DEV lever `carhit on|off` (cost check); probe state only

        private const int   DetailBudget  = 200;
        private const int   SummaryBudget = 120;
        private const float DedupeSec     = 1f;
        private const float SummaryEvery  = 60f;
        private const float RegRefreshSec = 2f;

        internal static readonly string[] ClassNames =
            { "driver", "parked-player", "traffic", "traffic-replica", "car-ghost", "parked-ambient", "parked-replica", "ghost-other", "other" };
        private const int CDriver = 0, CParked = 1, CTraffic = 2, CReplica = 3, CCarGhost = 4, CParkAmb = 5, CParkRep = 6, CGhostOther = 7, COther = 8, NC = 9;

        private static readonly int[,] _total    = new int[NC, NC];
        private static readonly int[,] _detailed = new int[NC, NC];
        private static readonly int[,] _window   = new int[NC, NC];   // driver pairs since the last summary
        private static int   _contacts, _deduped, _nonCar, _detailLines, _summaryLines, _driverCounted;
        private static bool  _budgetNoted;
        private static float _windowStart = -1f;

        private static readonly Dictionary<long, float> _lastPair = new Dictionary<long, float>();
        private static readonly Dictionary<int, int>    _regClass = new Dictionary<int, int>();
        private static readonly Dictionary<int, string> _regId    = new Dictionary<int, string>();
        private static float _regAt = -100f;
        private static readonly System.Action<string, string, string, GameObject> _regAdd = RegAdd;
        private static readonly StringBuilder _sb = new StringBuilder(256);

        private static void RegAdd(string kind, string model, string id, GameObject go)
        {
            try
            {
                if (go == null) return;
                int c = kind == "traffic-ghost" ? CReplica : kind == "pv-ghost" ? CCarGhost : kind == "parked-host" ? CParkAmb : kind == "parked-ghost" ? CParkRep : -1;
                if (c < 0) return;
                int gid = go.GetInstanceID();
                _regClass[gid] = c;
                _regId[gid] = id ?? "";
            }
            catch { }
        }

        private static void RefreshRegistries()
        {
            float now = Time.unscaledTime;
            if (now - _regAt < RegRefreshSec) return;
            _regAt = now;
            _regClass.Clear(); _regId.Clear();
            try { TrafficSync.ProbeGhostBodies(_regAdd); } catch { }
            try { VehicleManager.ProbeRemoteBodies(_regAdd); } catch { }
            try { ParkedVehicleSync.ProbeParkedBodies(_regAdd); } catch { }
        }

        /// <summary>Class of the body that owns <paramref name="go"/>; <paramref name="key"/> = the object the class came
        /// from (the dedupe identity), <paramref name="idObj"/> = what names it. -1 = not a car.</summary>
        private static int Classify(GameObject go, int selfLayer, Rigidbody? rb, out GameObject? key, out object? idObj)
        {
            key = go; idObj = null;
            if (go == null) return -1;
            var vc = go.GetComponentInParent<VehicleController>();
            if (vc != null && vc.controlledByPlayer)
            {
                try { if (vc == InstanceBehavior<GameManager>.Instance.selectedVehicle) { key = vc.gameObject; idObj = vc; return CDriver; } } catch { }
            }
            RefreshRegistries();
            var t = go.transform;
            for (int d = 0; d < 8 && t != null; d++, t = t.parent)
            {
                int c;
                if (_regClass.TryGetValue(t.gameObject.GetInstanceID(), out c)) { key = t.gameObject; idObj = t.gameObject.GetInstanceID(); return c; }
            }
            if (go.GetComponentInParent<ModGhostMarker>() != null)
            {
                if (vc != null) { key = vc.gameObject; idObj = vc; return CCarGhost; }   // a remote car's proxy nobody here drives
                key = go; idObj = go; return CGhostOther;
            }
            if (vc != null) { key = vc.gameObject; idObj = vc; return CParked; }
            var gley = go.GetComponentInParent<VehicleComponent>();
            if (gley != null) { key = gley.gameObject; idObj = gley; return CTraffic; }
            if (rb != null && go.layer == selfLayer) { key = rb.gameObject; idObj = rb.gameObject; return COther; }
            return -1;
        }

        private static string IdOf(int cls, GameObject? key, object? idObj)
        {
            try
            {
                if (idObj is VehicleController vc) return vc.vehicleInstance != null && !string.IsNullOrEmpty(vc.vehicleInstance.id) ? vc.vehicleInstance.id : "#" + vc.GetInstanceID();
                if (idObj is VehicleComponent g) return "gley" + g.GetIndex();
                if (idObj is int gid) { string s; return _regId.TryGetValue(gid, out s) ? s : "#" + gid; }
                if (idObj is GameObject go) return "'" + go.name + "'";
            }
            catch { }
            return "?";
        }

        /// <summary>The single entry point of both hooks. selfGo = the car that received the event.</summary>
        internal static void OnContact(GameObject selfGo, Collision collision, string hook)
        {
            try
            {
                if (!Armed || selfGo == null || collision == null) return;
                if (!MPServer.IsRunning && !MPClient.InMpGame) return;
                var otherCol = collision.collider;
                if (otherCol == null) return;
                var orb = otherCol.attachedRigidbody;
                var otherGo = orb != null ? orb.gameObject : otherCol.gameObject;

                GameObject? ka, kb; object? ia, ib;
                int ca = Classify(selfGo, selfGo.layer, null, out ka, out ia);
                if (ca < 0) return;
                int cb = Classify(otherGo, selfGo.layer, orb, out kb, out ib);
                if (cb < 0) { _nonCar++; return; }
                if (ka == null || kb == null || ka == kb) return;

                float now = Time.unscaledTime;
                int ida = ka.GetInstanceID(), idb = kb.GetInstanceID();
                long pk = ida < idb ? ((long)ida << 32) ^ (uint)idb : ((long)idb << 32) ^ (uint)ida;
                float last;
                if (_lastPair.TryGetValue(pk, out last) && now - last < DedupeSec) { _deduped++; return; }
                if (_lastPair.Count > 512) _lastPair.Clear();
                _lastPair[pk] = now;

                _contacts++;
                int lo = ca < cb ? ca : cb, hi = ca < cb ? cb : ca;
                _total[lo, hi]++;

                if (ca == CDriver || cb == CDriver)
                {
                    _driverCounted++;
                    if (_windowStart < 0f) _windowStart = now;
                    _window[lo, hi]++;
                    if (now - _windowStart >= SummaryEvery) FlushWindow(now);
                    return;
                }

                if (_detailLines >= DetailBudget)
                {
                    if (!_budgetNoted) { _budgetNoted = true; Plugin.Logger.LogWarning($"[CarHit] detail budget {DetailBudget} spent - contacts are now counted only (DEV lever carhit)."); }
                    return;
                }
                _detailLines++;
                _detailed[lo, hi]++;

                Vector3 pos = selfGo.transform.position;
                try { if (collision.contactCount > 0) pos = collision.GetContact(0).point; } catch { }
                float nearest = -1f;
                try
                {
                    var rps = RemotePlayerManager.GetRemotePlayerTransforms();
                    for (int i = 0; i < rps.Count; i++)
                    {
                        if (rps[i] == null) continue;
                        float d = Vector3.Distance(rps[i].position, pos);
                        if (nearest < 0f || d < nearest) nearest = d;
                    }
                }
                catch { }
                string sim = "?", mode = "?";
                try
                {
                    if (MPServer.IsRunning) { sim = "me"; mode = "own"; }
                    else
                    {
                        sim = TrafficSync.ClientRunsLocalTraffic ? "me" : "host";
                        mode = TrafficSync.ClientTrafficMode == "ghost" ? "ghost" : "own";
                    }
                }
                catch { }
                Plugin.Logger.LogWarning(
                    $"[CarHit] {ClassNames[ca]} {IdOf(ca, ka, ia)} x {ClassNames[cb]} {IdOf(cb, kb, ib)} impulse={collision.impulse.magnitude:F1} " +
                    $"relSpeed={collision.relativeVelocity.magnitude:F2} pos={pos.x:F1},{pos.z:F1} nearestRemotePlayer={(nearest < 0f ? "none" : nearest.ToString("F0"))} " +
                    $"trafficSim={sim} mode={mode} hook={hook} n={_detailLines}/{DetailBudget}");
            }
            catch (System.Exception ex) { try { TrafficSync.WarnOnce("CarHitProbe.OnContact", ex); } catch { } }
        }

        private static void FlushWindow(float now)
        {
            try
            {
                _windowStart = now;
                if (_summaryLines >= SummaryBudget) { System.Array.Clear(_window, 0, _window.Length); return; }
                _sb.Length = 0;
                int sum = 0;
                for (int a = 0; a < NC; a++)
                    for (int b = a; b < NC; b++)
                    {
                        int n = _window[a, b];
                        if (n == 0) continue;
                        sum += n;
                        _sb.Append(' ').Append(ClassNames[a]).Append('x').Append(ClassNames[b]).Append('=').Append(n);
                    }
                System.Array.Clear(_window, 0, _window.Length);
                if (sum == 0) return;
                _summaryLines++;
                Plugin.Logger.LogWarning($"[CarHit] driver contacts (counted, not detailed) last <=60s: {sum}{_sb} totalDriver={_driverCounted} line={_summaryLines}/{SummaryBudget}");
            }
            catch { }
        }

        /// <summary>DEV lever `carhit`: every class pair seen so far (total / detailed), plus the budgets.</summary>
        internal static string Readout()
        {
            var sb = new StringBuilder();
            try
            {
                sb.Append($"armed={Armed} contacts={_contacts} driverCounted={_driverCounted} detailed={_detailLines}/{DetailBudget} summaries={_summaryLines} deduped={_deduped} nonCar={_nonCar} pairs=[");
                bool first = true;
                for (int a = 0; a < NC; a++)
                    for (int b = a; b < NC; b++)
                    {
                        if (_total[a, b] == 0) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(ClassNames[a]).Append('x').Append(ClassNames[b]).Append('=').Append(_total[a, b]).Append('/').Append(_detailed[a, b]);
                    }
                sb.Append(']');
            }
            catch (System.Exception ex) { sb.Append(" err=").Append(ex.Message); }
            return sb.ToString();
        }
    }

    [HarmonyPatch(typeof(VehicleDeformationController), "OnCollisionEnter")]
    public static class Patch_VDC_OnCollisionEnter_CarHitProbe
    {
        static void Postfix(VehicleDeformationController __instance, Collision collision)
        {
            try { if (CarHitProbe.Armed && __instance != null) CarHitProbe.OnContact(__instance.gameObject, collision, "vdc"); } catch { }
        }
    }

    [HarmonyPatch(typeof(VehicleComponent), nameof(VehicleComponent.OnCollisionEnter))]
    public static class Patch_VC_OnCollisionEnter_CarHitProbe
    {
        static void Postfix(VehicleComponent __instance, Collision collision)
        {
            try { if (CarHitProbe.Armed && __instance != null) CarHitProbe.OnContact(__instance.gameObject, collision, "gley"); } catch { }
        }
    }
}
// PROBE-END: P-CARHIT
