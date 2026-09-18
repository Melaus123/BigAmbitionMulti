// PROBE-START: P-CUST-STALL (this whole file is the probe - delete the file to remove it)
// WHY: five player reports with clean logs - customers stuck at the entrance (visual only),
// stuck until a friend enters, stuck for a solo host, empty interiors on a client, a nightclub
// capped at 4. This probe separates four candidates WITHOUT changing anything:
//   (i)   NATIVE MOVER FREEZE - ThirdPersonCharacter.Update:269-285 freezes a body when
//         navmeshAgent.enabled==false || !isOnNavMesh || gameSpeed.Paused || Time.deltaTime==0,
//         while the behaviour tree and the sale keep running (so the customer "works" but
//         never moves).  => stall lines with agent en:N / mesh:N, or paused=Y / tscale 0.00.
//   (ii)  SPAWNER SUPPRESSION - IndoorCustomerSpawner.SpawningIsDisabled is a PROCESS-WIDE
//         static; CustomerPuppets.cs:264 sets it for a follower and restores at :106/:241/:304.
//         => suppressed=Y with follower=N is the leak.
//   (iii) FOLLOWER ELECTION replacing natives with stream puppets (CustomerPuppets.cs:175-207,
//         :255-300, _followerHere).  => follower=Y on the entry line.
//   (iv)  CAPS - Customers.Count >= buildingRegistration.customerCapacity
//         (IndoorCustomerSpawner.cs:196-199) and the nightclub DJ-booth gate
//         (NightclubBusinessHelper.cs:63-72 CanSpawnCustomers), with :209 burning the
//         CustomerEntry even when the spawn is refused.  => the 'capped' line.
// READ-ONLY: the only native call that is not a pure read is NightclubBusinessHelper
// .CanSpawnCustomers(), which refills its own private scratch list AvailableDjBooths (used
// nowhere else in that file - verified at :20/:69/:70/:71). No mod state, no protocol, no UI.
// Registry is IndoorCustomerSpawner.Customers (static List<Customer>, :19) - never a scene sweep.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Buildings;    // NightclubBusinessHelper
using Helpers;      // PlayerHelper
using UI;           // UIs.gameSpeed
using UnityEngine;
using UnityEngine.AI;

namespace BigAmbitionsMP
{
    /// <summary>P-CUST-STALL - entry snapshot (2s after DelayedEnterBuildingActions), a 60s stall
    /// sweep over the native customer registry, and a cap/DJ line. Log lines only.</summary>
    internal static class CustProbe
    {
        private const int   LineBudget   = 240;    // hard budget per session
        private const float SweepSeconds = 60f;
        private const float StallMeters  = 0.2f;   // movement under this does not reset the clock
        private const float StallSeconds = 30f;

        private static int   _lines;
        private static bool  _exhausted;
        private static int   _warns;
        private static float _enterAt;             // 0 = not armed
        private static float _nextSweepAt;

        // id -> (last position that counted as movement, when it was recorded)
        private static readonly Dictionary<int, (Vector3 pos, float since)> _seen = new Dictionary<int, (Vector3, float)>();
        private static readonly List<int> _gone = new List<int>();

        private static readonly System.Reflection.FieldInfo? _fSuppressed =
            AccessTools.Field(typeof(IndoorCustomerSpawner), "SpawningIsDisabled");
        private static readonly System.Reflection.FieldInfo? _fFollower =
            AccessTools.Field(typeof(CustomerPuppets), "_followerHere");

        private static IndoorCustomerSpawner? _spawner;

        private static string YN(bool b) => b ? "Y" : "N";

        private static void Emit(string line)
        {
            if (_lines >= LineBudget)
            {
                if (!_exhausted) { _exhausted = true; Plugin.Logger.LogInfo("[CustProbe] budget exhausted"); }
                return;
            }
            _lines++;
            Plugin.Logger.LogInfo(line);
        }

        private static void Warn(Exception ex)
        {
            if (_warns++ < 5) Plugin.Logger.LogWarning($"[CustProbe] probe error: {ex.Message}");
        }

        private static bool FollowerHere()
        {
            try { return _fFollower?.GetValue(null) is bool b && b; } catch { return false; }
        }

        private static string Suppressed()
        {
            try { return _fSuppressed?.GetValue(null) is bool b ? YN(b) : "-"; } catch { return "-"; }
        }

        private static bool Paused()
        {
            try { return InstanceBehavior<UIs>.Instance?.gameSpeed?.Paused ?? false; } catch { return false; }
        }

        private static bool OnMesh(Vector3 p)
        {
            try { return NavMesh.SamplePosition(p, out _, 1f, NavMesh.AllAreas); } catch { return false; }
        }

        // The game itself finds this component with FindObjectOfType (IndoorCustomerSpawner.cs:313);
        // there is no BuildingManager field for it. Cached, so at most one lookup per interior.
        private static string SpawnerEnabled()
        {
            try
            {
                if (_spawner == null) _spawner = UnityEngine.Object.FindObjectOfType<IndoorCustomerSpawner>();
                return _spawner == null ? "-" : YN(_spawner.enabled);
            }
            catch { return "-"; }
        }

        /// <summary>(a) ENTRY SNAPSHOT + (b) STALL SWEEP driver. Called from MPCanvasUI's pre-tick.</summary>
        public static void Tick()
        {
            try
            {
                float now = Time.unscaledTime;
                if (_enterAt > 0f && now - _enterAt >= 2f) { _enterAt = 0f; EmitEntry(); }
                if (now >= _nextSweepAt)
                {
                    _nextSweepAt = now + SweepSeconds;
                    if (BuildingManager.IsInsideBuilding) Sweep(now);
                    else { _seen.Clear(); _spawner = null; }
                }
            }
            catch (Exception ex) { Warn(ex); }
        }

        private static void EmitEntry()
        {
            try
            {
                if (!BuildingManager.IsInsideBuilding) return;
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null) return;
                var reg = bm.buildingRegistration;
                string addr = "-", biz = "-"; int cap = -1;
                if (reg != null)
                {
                    try { addr = GameStateReader.AddressKey(reg); } catch { }
                    biz = reg.businessTypeName ?? "-";
                    cap = reg.customerCapacity;
                }
                int exits = 0; string exit0 = "-";
                var zones = bm.exitZones;
                if (zones != null)
                {
                    exits = zones.Count;
                    if (exits > 0 && zones[0] != null) exit0 = YN(OnMesh(zones[0].transform.position));
                }
                string mesh = "-";
                try { mesh = YN(OnMesh(PlayerHelper.GetPosition())); } catch { }
                int live = 0; try { live = IndoorCustomerSpawner.Customers.Count; } catch { }

                Emit($"[CustProbe] enter '{addr}' biz={biz} open={YN(bm.isOpen)} owned={YN(bm.IsPlayerOwnedBusiness)}"
                   + $" cap={cap} mesh={mesh} exits={exits} exit0onMesh={exit0} spawner={SpawnerEnabled()}"
                   + $" suppressed={Suppressed()} follower={YN(FollowerHere())} skipActive={YN(MPRestSync.SkipActive)}"
                   + $" paused={YN(Paused())} tscale={Time.timeScale:F2} live={live}");
            }
            catch (Exception ex) { Warn(ex); }
        }

        private static void Sweep(float now)
        {
            var bm = InstanceBehavior<BuildingManager>.Instance;
            if (bm == null) return;
            var reg = bm.buildingRegistration;
            string addr = "-"; int cap = -1;
            if (reg != null)
            {
                try { addr = GameStateReader.AddressKey(reg); } catch { }
                cap = reg.customerCapacity;
            }
            var zones = bm.exitZones;
            var list = IndoorCustomerSpawner.Customers;
            int live = list.Count, stalled = 0;
            var stalls = new List<string>(3);

            for (int i = 0; i < live; i++)
            {
                var c = list[i];
                if (c == null) continue;
                int id = c.GetInstanceID();
                Vector3 p = c.transform.position;
                if (!_seen.TryGetValue(id, out var rec)) { _seen[id] = (p, now); continue; }
                if ((p - rec.pos).magnitude >= StallMeters) { _seen[id] = (p, now); continue; }
                float still = now - rec.since;
                if (still < StallSeconds) continue;

                var tpc = c.tpc;
                bool walking = false;
                try { walking = tpc != null && tpc.isWalkingTowardsTarget; } catch { }
                bool early = (int)c.state <= (int)CustomerState.GoingToWaitingLine;
                if (!walking && !early) continue;
                stalled++;
                if (stalls.Count >= 3) continue;

                string d2door = "-";
                if (zones != null && zones.Count > 0)
                {
                    float best = float.MaxValue;
                    for (int z = 0; z < zones.Count; z++)
                    {
                        if (zones[z] == null) continue;
                        float d = (zones[z].transform.position - p).magnitude;
                        if (d < best) best = d;
                    }
                    if (best < float.MaxValue) d2door = best.ToString("F1");
                }

                string reached = "-", vis = "-", agent = "-";
                if (tpc != null)
                {
                    try { reached = YN(tpc.WasLastTargetReached); } catch { }
                    try { vis = YN(tpc.visible); } catch { }
                    try
                    {
                        var ag = tpc.navmeshAgent;
                        if (ag != null)
                        {
                            bool en = ag.enabled;
                            bool om = en && ag.isOnNavMesh;   // the other agent reads are illegal off-mesh
                            agent = $"en:{YN(en)} mesh:{YN(om)} stopped:{(om ? YN(ag.isStopped) : "-")}"
                                  + $" path:{(om ? YN(ag.hasPath) : "-")}/{(om ? ag.pathStatus.ToString() : "-")}"
                                  + $" vel:{(en ? ag.velocity.magnitude.ToString("F2") : "-")}";
                        }
                    }
                    catch { }
                }
                string bt = "-";
                try { bt = c.behaviorTree != null ? YN(c.behaviorTree.enabled) : "-"; } catch { }

                stalls.Add($"[CustProbe]  stall id={id} st={c.state} still={still:F0}s d2door={d2door}m"
                         + $" walk={YN(walking)} reached={reached} agent={agent} vis={vis} bt={bt}");
            }

            // drop ids that left the registry (once a minute, so the scan cost is irrelevant)
            _gone.Clear();
            foreach (var kv in _seen)
            {
                bool found = false;
                for (int i = 0; i < live; i++) { var c = list[i]; if (c != null && c.GetInstanceID() == kv.Key) { found = true; break; } }
                if (!found) _gone.Add(kv.Key);
            }
            for (int i = 0; i < _gone.Count; i++) _seen.Remove(_gone[i]);

            Emit($"[CustProbe] sweep '{addr}' live={live}/{cap} stalled={stalled} paused={YN(Paused())}"
               + $" tscale={Time.timeScale:F2} follower={YN(FollowerHere())}");
            for (int i = 0; i < stalls.Count; i++) Emit(stalls[i]);

            // (c) CAP LINE
            if (live >= cap || cap <= 0)
            {
                string dj = "n/a";
                try
                {
                    if (bm.businessType != null && bm.businessType.customerType == CustomerType.Nightclub)
                        dj = YN(NightclubBusinessHelper.CanSpawnCustomers());
                }
                catch { dj = "-"; }
                Emit($"[CustProbe] capped '{addr}' live={live} cap={cap} dj={dj}");
            }
        }

        /// <summary>Arms the 2s entry snapshot. Same target EntryBailProbe.cs:91 patches.</summary>
        [HarmonyPatch(typeof(BuildingManager), "DelayedEnterBuildingActions")]
        public static class Patch_CustProbe_Enter
        {
            static void Postfix()
            {
                try { _enterAt = Time.unscaledTime; _seen.Clear(); _spawner = null; }
                catch (Exception ex) { Warn(ex); }
            }
        }
    }
    // PROBE-END: P-CUST-STALL
}
