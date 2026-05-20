using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Helpers;
using Il2CppInterop.Runtime;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Backlog #3 — parked-vehicle sync (street parking + parking lots).
    ///
    /// Architecture (per the ProbeParkedVehicles dump 2026-05-19):
    /// - 558 distinct ParkingLaneGenerator MonoBehaviour instances spread
    ///   across the city, each runs its own RNG-driven GenerateParkedVehicles.
    /// - Vehicles are rented from a shared static pool: Helpers.ParkingSimulator.
    ///   .RequestParkedVehicle(string name) / .ReleaseParkedVehicle(GameObject).
    /// - Regeneration is time-driven: ParkingSimulator.RunHourly + .RunDaily,
    ///   with a UnityEvent ParkingLaneRegeneration that fires on each cycle.
    /// - Each lane exposes Action`1 onGenerateVehicle / onReleaseVehicle hooks
    ///   per spawn/release — clean integration points without Harmony patches
    ///   into the generation code itself.
    ///
    /// Phase 3a (this iteration): HOST-SIDE CAPTURE ONLY, no network yet.
    /// Subscribe every lane's onGenerate/onRelease events; maintain an
    /// in-memory map of currently-parked vehicles {GO instance id → dto-shape}.
    /// Log periodic counts so we can verify capture works before shipping
    /// anything across the wire in Phase 3b.
    /// </summary>
    public static class ParkedVehicleSync
    {
        // Currently-tracked parked vehicles on the host.  Keyed by the
        // GameObject's instance id (stable for the GameObject's lifetime).
        public class ParkedEntry
        {
            public int       InstanceId;
            public string    Model = "";
            public Vector3   Pos;
            public Quaternion Rot;
            // Colors filled lazily on first capture; SH_Vehicle has the same
            // two non-`_`-prefixed Color props as traffic cars.
            public List<float>? Colors;
        }

        private static readonly Dictionary<int, ParkedEntry> _entries = new();

        private static bool _wired;
        private static int  _wiredLaneCount;
        private static float _lastLogTime;

        public static int Count => _entries.Count;
        public static IReadOnlyDictionary<int, ParkedEntry> Entries => _entries;

        public static void Reset()
        {
            _wired = false;
            _wiredLaneCount = 0;
            _lastLogTime = 0f;
            _entries.Clear();
        }

        /// <summary>One-shot per-game wire-up + periodic diagnostic log.
        /// Called from MPCanvasUI.TickPositionSync on the host only.</summary>
        public static void Tick()
        {
            try
            {
                if (!MPServer.IsRunning) return;
                if (SaveGameManager.Current == null) return;
                if (Time.timeSinceLevelLoad < 5f) return;       // let lanes init

                if (!_wired) WireAllLanes();

                // Periodic count log so we can watch the capture working over
                // an hourly regen cycle without flooding the log.
                if (Time.unscaledTime - _lastLogTime > 30f)
                {
                    _lastLogTime = Time.unscaledTime;
                    Plugin.Logger.LogInfo(
                        $"[ParkedSync] tracked vehicles={_entries.Count} (lanes wired={_wiredLaneCount})");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] Tick: {ex.Message}");
            }
        }

        private static void WireAllLanes()
        {
            try
            {
                var laneT = FindType("ParkingLaneGenerator");
                if (laneT == null)
                {
                    Plugin.Logger.LogWarning("[ParkedSync] ParkingLaneGenerator type not found — will retry.");
                    return;
                }

                var il2 = Il2CppType.From(laneT);
                var lanes = UnityEngine.Object.FindObjectsOfType(il2);
                if (lanes == null || lanes.Length == 0)
                {
                    Plugin.Logger.LogInfo("[ParkedSync] no ParkingLaneGenerator instances yet — will retry.");
                    return;
                }

                int subbed = 0;
                var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var genProp  = laneT.GetProperty("onGenerateVehicle", bf);
                var relProp  = laneT.GetProperty("onReleaseVehicle",  bf);
                if (genProp == null || relProp == null)
                {
                    Plugin.Logger.LogWarning(
                        $"[ParkedSync] missing onGenerate/onRelease properties on lane (gen={genProp != null} rel={relProp != null}).");
                    return;
                }

                // The Action<T> is exposed as a property — we ADD to it.  Reading
                // returns the current delegate (may be null); we Combine with our
                // handler and write back.
                var actT = genProp.PropertyType;     // Action<GameObject> (or similar)
                var mi = typeof(ParkedVehicleSync).GetMethod(nameof(OnLaneGenerated),
                            BindingFlags.NonPublic | BindingFlags.Static)!;
                var mi2 = typeof(ParkedVehicleSync).GetMethod(nameof(OnLaneReleased),
                            BindingFlags.NonPublic | BindingFlags.Static)!;

                foreach (var obj in lanes)
                {
                    try
                    {
                        // genProp / relProp are Action<GameObject> properties.  We
                        // build a delegate of the matching type pointing at our
                        // static handler.
                        var genDel = Delegate.CreateDelegate(genProp.PropertyType, mi);
                        var existingGen = genProp.GetValue(obj);
                        var combinedGen = Delegate.Combine((Delegate?)existingGen, genDel);
                        genProp.SetValue(obj, combinedGen);

                        var relDel = Delegate.CreateDelegate(relProp.PropertyType, mi2);
                        var existingRel = relProp.GetValue(obj);
                        var combinedRel = Delegate.Combine((Delegate?)existingRel, relDel);
                        relProp.SetValue(obj, combinedRel);

                        subbed++;
                    }
                    catch (Exception ex)
                    {
                        if (subbed < 3)     // log first few failures
                            Plugin.Logger.LogWarning($"[ParkedSync] subscribe lane failed: {ex.Message}");
                    }
                }

                _wiredLaneCount = subbed;
                _wired = subbed > 0;
                Plugin.Logger.LogInfo(
                    $"[ParkedSync] subscribed onGenerate/onRelease on {subbed}/{lanes.Length} lanes.");
                // NOTE: ParkingSimulator.ParkingLaneRegeneration UnityEvent
                // subscription deferred — IL2CPP's UnityAction ctor takes
                // IntPtr, needs a different bridging shim.  Not required for
                // Phase 3a because the per-lane Action delegates already fire
                // per individual spawn/release.
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ParkedSync] WireAllLanes: {ex.Message}");
            }
        }

        private static void OnLaneGenerated(GameObject go)
        {
            try
            {
                if (go == null) return;
                int id = go.GetInstanceID();
                var t  = go.transform;
                var entry = new ParkedEntry
                {
                    InstanceId = id,
                    Model      = StripCloneSuffix(go.name),
                    Pos        = t.position,
                    Rot        = t.rotation,
                };
                _entries[id] = entry;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] OnLaneGenerated: {ex.Message}");
            }
        }

        private static void OnLaneReleased(GameObject go)
        {
            try
            {
                if (go == null) return;
                int id = go.GetInstanceID();
                _entries.Remove(id);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] OnLaneReleased: {ex.Message}");
            }
        }

        private static Type? FindType(string nameOrFullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t.FullName == nameOrFullName || t.Name == nameOrFullName) return t;
                }
            }
            return null;
        }

        private static string StripCloneSuffix(string name)
        {
            int idx = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx >= 0 ? name.Substring(0, idx) : name;
        }
    }
}
