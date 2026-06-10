using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Helpers;
using Vehicles.VehicleTypes;
using GleyTrafficSystem;
using Il2CppInterop.Runtime;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Vehicle subsystem.  Currently a discovery probe — it gathers the data we
    /// need to design vehicle sync (the vehicle class landscape, how the player
    /// references / attaches to a vehicle, and the entry/exit API).  Will grow
    /// into the actual sync: transform sync + driver association + identity.
    ///
    /// All public methods must be called on the Unity main thread.
    /// </summary>
    public static class VehicleManager
    {
        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static bool _assembliesScanned;
        private static bool _playerMembersProbed;
        private static bool _parentInit;
        private static Transform? _lastCharParent;

        /// <summary>One discovery entry point — called every frame; sub-probes self-guard.</summary>
        public static void ProbeVehicles()
        {
            try
            {
                ScanAssembliesForVehicleTypes();   // once — the class landscape
                ProbePlayerMembers();              // once — how the player refs a vehicle
                ProbeParentChanges();              // live — dumps the vehicle on enter/exit
                ProbeVehicleSystem();              // once — the vehicle-system class APIs
                ProbeDrivenVehicle();              // live — dumps the driven vehicle once
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] ProbeVehicles: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── 4. Vehicle-system class API dump ──────────────────────────────────
        //
        // Dumps the declared members of the classes that drive vehicle spawning,
        // control and identity — the data we need to design "recreate a remote
        // player's vehicle on my screen".

        private static bool _systemProbed;

        private static readonly string[] _systemTypes =
        {
            "Helpers.VehicleHelper",
            "VehicleSpawnerController",
            "VehicleController",
            "CarController",
            "CarFeatures",
            "VehicleInstance",
            "Vehicles.VehicleTypes.VehicleTypeHelper",
            "Vehicles.VehicleTypes.VehicleType",
            "Vehicles.VehicleTypes.VehicleTypeName",
            "Entities.VehicleSlot",
            "Data.VehicleColors.VehicleColor",
        };

        private static void ProbeVehicleSystem()
        {
            if (_systemProbed) return;
            if (PlayerHelper.PlayerController == null) return;   // wait until in-game
            _systemProbed = true;

            Plugin.Logger.LogInfo("[Vehicle] === Vehicle-system class API dump ===");
            foreach (var name in _systemTypes)
                DumpTypeMembers(name);
        }

        private static void DumpTypeMembers(string fullName)
        {
            var t = FindGameType(fullName);
            if (t == null)
            {
                Plugin.Logger.LogWarning($"[Vehicle]   type not found: {fullName}");
                return;
            }
            // DeclaredOnly — only this type's own API, not inherited MonoBehaviour noise.
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.DeclaredOnly;
            Plugin.Logger.LogInfo($"[Vehicle] ### {t.FullName} ###");
            try
            {
                Plugin.Logger.LogInfo("[Vehicle]   props:   " +
                    string.Join(" | ", t.GetProperties(f).Select(p => $"{p.Name}({p.PropertyType.Name})")));
                Plugin.Logger.LogInfo("[Vehicle]   fields:  " +
                    string.Join(" | ", t.GetFields(f).Select(x => $"{x.Name}({x.FieldType.Name})")));
                Plugin.Logger.LogInfo("[Vehicle]   methods: " +
                    string.Join(" | ", t.GetMethods(f).Where(m => !m.IsSpecialName)
                        .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name))})")));
                if (t.IsEnum)
                    Plugin.Logger.LogInfo("[Vehicle]   enum values: " + string.Join(", ", Enum.GetNames(t)));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle]   DumpTypeMembers '{fullName}': {ex.Message}");
            }
        }

        public static Type? FindGameType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullName); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        /// <summary>Every method with the given simple name across all loaded
        /// assemblies.  Used by Harmony patches whose declaring type isn't known
        /// at compile time AND which may exist on multiple types (base +
        /// derived).  Logs each match so we can see what's being patched.</summary>
        public static IEnumerable<System.Reflection.MethodBase> FindAllMethodsByName(string methodName)
        {
            var bf = System.Reflection.BindingFlags.Public
                   | System.Reflection.BindingFlags.NonPublic
                   | System.Reflection.BindingFlags.Instance
                   | System.Reflection.BindingFlags.Static
                   | System.Reflection.BindingFlags.DeclaredOnly;   // skip inherited
            int hits = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    System.Reflection.MethodInfo? m = null;
                    try { m = t.GetMethod(methodName, bf); } catch { }
                    if (m != null)
                    {
                        hits++;
                        Plugin.Logger.LogInfo($"[FindMethod] '{methodName}' → {t.FullName}");
                        yield return m;
                    }
                }
            }
            if (hits == 0)
                Plugin.Logger.LogWarning($"[FindMethod] '{methodName}' not found in any loaded assembly.");
        }

        // ── 5. Driven-vehicle dump — model + colour identity ──────────────────
        //
        // While the player is driving, dumps the vehicle GameObject's renderers
        // and materials — the data needed to identify the vehicle model and its
        // colour so a remote copy can be recreated to match.

        private static bool _drivenDumped;

        private static void ProbeDrivenVehicle()
        {
            if (_drivenDumped) return;
            var character = PlayerHelper.PlayerController?.Character;
            if (character == null) return;
            var parent = character.transform.parent;
            if (parent == null) return;

            var vc = FindComponentByName(parent.gameObject, "VehicleController");
            if (vc == null) return;                       // not in a vehicle
            _drivenDumped = true;

            Plugin.Logger.LogInfo($"[Vehicle] === Driven vehicle '{parent.name}' ===");

            // Components on the vehicle root
            DumpNode(parent, 0);

            // Renderers + materials (model + colour identity)
            try
            {
                var rends = parent.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                Plugin.Logger.LogInfo($"[Vehicle]   renderers ({rends.Length}):");
                for (int i = 0; i < rends.Length && i < 40; i++)
                {
                    var r = rends[i].TryCast<Renderer>();
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    var ms = new List<string>();
                    for (int m = 0; m < mats.Length; m++)
                        if (mats[m] != null)
                            ms.Add($"{mats[m].name}/{(mats[m].shader != null ? mats[m].shader.name : "?")}");
                    Plugin.Logger.LogInfo(
                        $"[Vehicle]     '{r.gameObject.name}' [{string.Join(", ", ms)}]");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle]   renderer dump: {ex.Message}");
            }
        }

        public static Component? FindComponentByName(GameObject go, string typeName)
        {
            try
            {
                var comps = go.GetComponents(Il2CppType.Of<Component>());
                for (int i = 0; i < comps.Length; i++)
                    if (comps[i] != null && comps[i].GetIl2CppType().Name == typeName)
                        return comps[i];
            }
            catch { }
            return null;
        }

        // ── 1. Assembly scan — every vehicle-named type ───────────────────────

        // "car" alone matches noise (di-scar-dable, generi-cAr-ray…) so we don't
        // keyword on it — CarController/CarFeatures are already known by name.
        private static readonly string[] _typeKeywords =
        {
            "vehicle", "scooter", "motorcycle", "motorbike", "moped"
        };

        private static void ScanAssembliesForVehicleTypes()
        {
            if (_assembliesScanned) return;
            _assembliesScanned = true;

            Plugin.Logger.LogInfo("[Vehicle] === Assembly scan: vehicle-named types ===");
            int found = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t.Name == null) continue;
                    string lower = t.Name.ToLowerInvariant();
                    bool hit = false;
                    foreach (var kw in _typeKeywords)
                        if (lower.Contains(kw)) { hit = true; break; }
                    if (!hit) continue;

                    Plugin.Logger.LogInfo($"[Vehicle]   {t.FullName}");
                    found++;
                }
            }
            Plugin.Logger.LogInfo($"[Vehicle] === Assembly scan done: {found} type(s) ===");
        }

        // ── 2. Player member dump — how the player references a vehicle ───────

        private static void ProbePlayerMembers()
        {
            if (_playerMembersProbed) return;
            try
            {
                var pc = PlayerHelper.PlayerController;
                if (pc == null) return;                      // not ready — retry next frame
                var character = pc.Character;
                if (character == null) return;
                _playerMembersProbed = true;

                DumpMembers("PlayerController", pc);
                DumpMembers("ThirdPersonCharacter (Character)", character);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] ProbePlayerMembers: {ex.Message}");
            }
        }

        private static void DumpMembers(string label, object obj)
        {
            try
            {
                var t = obj.GetType();
                Plugin.Logger.LogInfo($"[Vehicle] --- {label} : {t.FullName} ---");
                Plugin.Logger.LogInfo("[Vehicle] props:   " +
                    string.Join(" | ", t.GetProperties(Flags)
                        .Select(p => $"{p.Name}({p.PropertyType.Name})")));
                Plugin.Logger.LogInfo("[Vehicle] methods: " +
                    string.Join(" | ", t.GetMethods(Flags).Where(m => !m.IsSpecialName)
                        .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name))})")));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle] DumpMembers '{label}': {ex.Message}");
            }
        }

        // ── 3. Parent-change probe — dump the vehicle on enter/exit ───────────
        //
        // If entering a vehicle reparents the character (common in Unity games),
        // this catches it: when the character's transform parent changes we dump
        // the full ancestor chain with components — which reveals the vehicle
        // GameObject and its controller class.

        private static void ProbeParentChanges()
        {
            var character = PlayerHelper.PlayerController?.Character;
            if (character == null) return;

            var parent = character.transform.parent;
            if (!_parentInit)
            {
                _parentInit = true;
                _lastCharParent = parent;
                return;
            }
            if (parent == _lastCharParent) return;
            _lastCharParent = parent;

            Plugin.Logger.LogInfo(
                $"[Vehicle] === Character parent changed → '{(parent != null ? parent.name : "(none)")}' " +
                "— dumping ancestor chain ===");

            var t = character.transform;
            int depth = 0;
            while (t != null && depth < 14)
            {
                DumpNode(t, depth);
                t = t.parent;
                depth++;
            }
        }

        private static void DumpNode(Transform t, int depth)
        {
            string indent = new string(' ', depth * 2);
            string comps;
            try
            {
                var cs = t.gameObject.GetComponents(Il2CppType.Of<Component>());
                var names = new List<string>();
                for (int i = 0; i < cs.Length; i++)
                    if (cs[i] != null) names.Add(cs[i].GetIl2CppType().Name);
                comps = string.Join(", ", names);
            }
            catch { comps = "(components unavailable)"; }

            Plugin.Logger.LogInfo(
                $"[Vehicle]   {indent}{t.name}  active={t.gameObject.activeSelf}  [{comps}]");
        }

        // ── Traffic-lights + colour discovery probe ───────────────────────────
        //
        // Dumps the GleyTrafficSystem intersection/light classes and the
        // vehicle-colour classes — the data needed to design traffic-light sync
        // and traffic-car colour sync.

        // ── ProbeParkedVehicles (backlog #3 discovery) ────────────────────────
        // The world has parked cars in lots + along streets that DON'T sync
        // between host and client.  Game symbols dumped earlier:
        //   _parkedVehiclesStorage, _numberOfParkedVehicles, ParkedVehiclePools,
        //   GenerateParkedVehicles, RequestParkedVehicle, ReleaseParkedVehicle,
        //   CleanupParkedVehicles, GetVehiclesInParkingSpots,
        //   ParkingLaneGenerator, ParkingLaneRegeneration, ParkingVehiclesIndices
        // We don't know which class owns these yet, nor the shape of the
        // storage entries (position? rotation? prefab? color?).  This probe
        // finds the owning class, logs its members, samples the storage, and
        // identifies the spawn / release API.

        private static bool _parkedProbed;

        public static void ProbeParkedVehicles()
        {
            if (_parkedProbed) return;
            if (SaveGameManager.Current == null) return;
            if (Time.timeSinceLevelLoad < 25f) return;     // wait for world to settle
            _parkedProbed = true;

            Plugin.Logger.LogInfo("[Parked] === ProbeParkedVehicles START (round 2) ===");
            try
            {
                // Round-1 observed: each parking strip is its own MonoBehaviour
                // (ParkingLaneGenerator); Helpers.ParkingSimulator is a static
                // pool of parked-vehicle GameObjects.  Round 2 dumps the actual
                // members of those types and samples live instances.
                var bfAll = BindingFlags.Public | BindingFlags.NonPublic
                          | BindingFlags.Instance | BindingFlags.Static;   // no DeclaredOnly

                // Find ParkingLaneGenerator type by full name in any assembly.
                System.Type? laneT = null;
                System.Type? simT  = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    System.Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.Name == "ParkingLaneGenerator" && laneT == null) laneT = t;
                        if (t.FullName == "Helpers.ParkingSimulator" && simT == null) simT = t;
                    }
                }
                if (laneT == null) { Plugin.Logger.LogWarning("[Parked] ParkingLaneGenerator type not found."); return; }
                if (simT  == null) Plugin.Logger.LogWarning("[Parked] Helpers.ParkingSimulator type not found.");

                Plugin.Logger.LogInfo($"[Parked] ParkingLaneGenerator → {laneT.FullName}");
                if (simT != null) Plugin.Logger.LogInfo($"[Parked] ParkingSimulator    → {simT.FullName}");

                // ── A. Full member dump of ParkingLaneGenerator ──────────────
                Plugin.Logger.LogInfo("[Parked] === ParkingLaneGenerator members ===");
                DumpAllFields(laneT, bfAll, "[Parked]   field  ");
                DumpAllProps (laneT, bfAll, "[Parked]   prop   ");

                // ── B. Full member dump of Helpers.ParkingSimulator ──────────
                if (simT != null)
                {
                    Plugin.Logger.LogInfo("[Parked] === ParkingSimulator members ===");
                    DumpAllFields(simT, bfAll, "[Parked]   field  ");
                    DumpAllProps (simT, bfAll, "[Parked]   prop   ");
                }

                // ── C. Find live ParkingLaneGenerator instances + sample ─────
                Plugin.Logger.LogInfo("[Parked] === live ParkingLaneGenerator instances ===");
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object>? lanes = null;
                try
                {
                    var il2 = Il2CppType.From(laneT);
                    lanes = UnityEngine.Object.FindObjectsOfType(il2);
                }
                catch (System.Exception ex)
                {
                    Plugin.Logger.LogWarning($"[Parked] FindObjectsOfType(ParkingLaneGenerator) failed: {ex.Message}");
                }
                int laneCount = lanes != null ? lanes.Length : 0;
                Plugin.Logger.LogInfo($"[Parked]   live ParkingLaneGenerator count: {laneCount}");

                // Sample first lane: dump every reflected field value.
                if (lanes != null && lanes.Length > 0)
                {
                    var lane0 = lanes[0];
                    Plugin.Logger.LogInfo($"[Parked]   --- sample lane[0] ({lane0?.GetType().Name}) ---");
                    DumpInstanceFields(lane0, laneT, bfAll, "[Parked]   lane0  ");
                }

                // ── D. Static state of ParkingSimulator (the pool) ───────────
                if (simT != null)
                {
                    Plugin.Logger.LogInfo("[Parked] === ParkingSimulator static state ===");
                    foreach (var f in simT.GetFields(bfAll).Where(f => f.IsStatic).Take(20))
                    {
                        object? v = null;
                        try { v = f.GetValue(null); } catch { }
                        Plugin.Logger.LogInfo($"[Parked]   simStatic  {f.Name}({f.FieldType.Name}) = {DescribeValue(v)}");
                    }
                }

                // ── E. List every method (sig + static) on both types ───────
                Plugin.Logger.LogInfo("[Parked] === ParkingLaneGenerator methods ===");
                foreach (var m in laneT.GetMethods(bfAll).Where(m => m.DeclaringType == laneT).Take(40))
                {
                    var ps = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name));
                    Plugin.Logger.LogInfo($"[Parked]   lm   {m.Name}({ps}) : {m.ReturnType.Name} (static={m.IsStatic})");
                }
                if (simT != null)
                {
                    Plugin.Logger.LogInfo("[Parked] === ParkingSimulator methods ===");
                    foreach (var m in simT.GetMethods(bfAll).Where(m => m.DeclaringType == simT).Take(40))
                    {
                        var ps = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name));
                        Plugin.Logger.LogInfo($"[Parked]   sm   {m.Name}({ps}) : {m.ReturnType.Name} (static={m.IsStatic})");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"[Parked] probe error: {ex.Message}\n{ex.StackTrace}");
            }
            Plugin.Logger.LogInfo("[Parked] === ProbeParkedVehicles END ===");
        }

        // ── helpers — only used by ProbeParkedVehicles ───────────────────────
        private static void DumpAllFields(System.Type t, BindingFlags bf, string prefix)
        {
            try
            {
                foreach (var f in t.GetFields(bf).Where(f => f.DeclaringType == t).Take(60))
                    Plugin.Logger.LogInfo($"{prefix}{f.Name}({f.FieldType.Name}) static={f.IsStatic}");
            }
            catch (System.Exception ex)
            { Plugin.Logger.LogWarning($"{prefix}<dump failed: {ex.Message}>"); }
        }
        private static void DumpAllProps(System.Type t, BindingFlags bf, string prefix)
        {
            try
            {
                foreach (var p in t.GetProperties(bf).Where(p => p.DeclaringType == t).Take(60))
                {
                    bool isStatic = (p.GetGetMethod(true)?.IsStatic ?? false) || (p.GetSetMethod(true)?.IsStatic ?? false);
                    Plugin.Logger.LogInfo($"{prefix}{p.Name}({p.PropertyType.Name}) static={isStatic}");
                }
            }
            catch (System.Exception ex)
            { Plugin.Logger.LogWarning($"{prefix}<dump failed: {ex.Message}>"); }
        }
        private static void DumpInstanceFields(object? inst, System.Type t, BindingFlags bf, string prefix)
        {
            if (inst == null) { Plugin.Logger.LogWarning($"{prefix}<null instance>"); return; }
            try
            {
                foreach (var f in t.GetFields(bf).Where(f => !f.IsStatic && f.DeclaringType == t).Take(40))
                {
                    object? v = null;
                    try { v = f.GetValue(inst); } catch (System.Exception ex) { v = $"<err: {ex.Message}>"; }
                    Plugin.Logger.LogInfo($"{prefix}{f.Name} = {DescribeValue(v)}");
                }
            }
            catch (System.Exception ex)
            { Plugin.Logger.LogWarning($"{prefix}<dump failed: {ex.Message}>"); }
        }
        private static string DescribeValue(object? v)
        {
            if (v == null) return "<null>";
            try
            {
                var t = v.GetType();
                // Collection?  Show count.
                var pLen = t.GetProperty("Length"); if (pLen != null) return $"<{t.Name} len={pLen.GetValue(v)}>";
                var pCnt = t.GetProperty("Count");  if (pCnt != null) return $"<{t.Name} count={pCnt.GetValue(v)}>";
                string s = v.ToString() ?? "<null-string>";
                if (s.Length > 80) s = s.Substring(0, 80) + "…";
                return $"{s} <{t.Name}>";
            }
            catch (System.Exception ex) { return $"<describe-err: {ex.Message}>"; }
        }

        private static bool _trafficExtrasProbed;

        public static void ProbeTrafficExtras()
        {
            if (_trafficExtrasProbed) return;
            if (SaveGameManager.Current == null) return;
            if (Time.timeSinceLevelLoad < 25f) return;
            _trafficExtrasProbed = true;

            Plugin.Logger.LogInfo("[TrafficExtras] === Lights + colour class dump ===");
            foreach (var name in new[]
            {
                "GleyTrafficSystem.IntersectionManager",
                "GleyTrafficSystem.TrafficLightsIntersection",
                "GleyTrafficSystem.TrafficLightsColor",
                "GleyTrafficSystem.TrafficLightsBehaviour",
                "GleyTrafficSystem.TrafficLightsBehaviours",
                "GleyTrafficSystem.PriorityIntersection",
                "RandomVehicleColor",
                "Data.VehicleColors.VehicleColor",
                "Data.VehicleColors.VehicleColors",
            })
                DumpTypeMembers(name);
            Plugin.Logger.LogInfo("[TrafficExtras] === dump done ===");
        }

        // ── Traffic-car colour discovery probe ────────────────────────────────
        //
        // Logs a live traffic car's renderers, materials, and Color shader
        // properties — both the shared-material value and the per-renderer
        // MaterialPropertyBlock value — so we can see where the car's body
        // colour lives and design colour sync.

        private static bool _carColorProbed;

        public static void ProbeCarColor()
        {
            if (_carColorProbed) return;
            if (SaveGameManager.Current == null) return;
            if (Time.timeSinceLevelLoad < 25f) return;
            try
            {
                var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<VehicleComponent>());
                if (arr == null || arr.Length == 0) return;

                // Skip fixed-livery vehicles (truck/taxi/emergency) — sample
                // ordinary cars, which are the ones that get random colours.
                string[] skip = { "Truck", "Taxi", "Ambulance", "Police", "Delivery", "Freight" };
                var samples = new List<GameObject>();
                for (int i = 0; i < arr.Length && samples.Count < 3; i++)
                {
                    var c = arr[i].TryCast<Component>();
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    string nm = c.gameObject.name;
                    bool fixedLivery = false;
                    foreach (var s in skip) if (nm.Contains(s)) { fixedLivery = true; break; }
                    if (fixedLivery) continue;
                    samples.Add(c.gameObject);
                }
                if (samples.Count == 0) return;             // only livery vehicles around — retry
                _carColorProbed = true;

                var mpb = new MaterialPropertyBlock();
                foreach (var car in samples)
                {
                    Plugin.Logger.LogInfo($"[CarColor] === Probe: traffic car '{car.name}' ===");
                    var rends = car.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                    for (int i = 0; i < rends.Length && i < 24; i++)
                    {
                        var r = rends[i].TryCast<Renderer>();
                        if (r == null) continue;
                        // SH_Vehicle body renderers are what carry the car colour.
                        var s0 = r.sharedMaterial;
                        bool isBody = s0 != null && s0.shader != null && s0.shader.name.Contains("SH_Vehicle");
                        if (!isBody) continue;
                        r.GetPropertyBlock(mpb);
                        var mats = r.sharedMaterials;
                        for (int m = 0; m < mats.Length; m++)
                        {
                            var mat = mats[m];
                            if (mat == null || mat.shader == null) continue;
                            var sh = mat.shader;
                            var cols = new List<string>();
                            int n = sh.GetPropertyCount();
                            for (int p = 0; p < n; p++)
                            {
                                if (sh.GetPropertyType(p) != UnityEngine.Rendering.ShaderPropertyType.Color)
                                    continue;
                                string pn = sh.GetPropertyName(p);
                                cols.Add($"{pn}[shared={mat.GetColor(pn)} mpb={mpb.GetColor(pn)}]");
                            }
                            Plugin.Logger.LogInfo(
                                $"[CarColor]   '{r.gameObject.name}' mat='{mat.name}' shader='{sh.name}' " +
                                $"mpbEmpty={mpb.isEmpty} :: {string.Join(", ", cols)}");
                        }
                    }
                }
                Plugin.Logger.LogInfo("[CarColor] === done ===");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[CarColor] ProbeCarColor: {ex.Message}");
            }
        }

        // ── Taxi discovery probe ──────────────────────────────────────────────
        //
        // Determines whether a taxi's fast-travel interaction (TaxiController)
        // can survive on a synced ghost: dumps the TaxiController API and a live
        // taxi's component layout, so we know its dependencies and the click hook.

        private static bool _taxiProbed;

        public static void ProbeTaxi()
        {
            if (_taxiProbed) return;
            if (SaveGameManager.Current == null) return;
            if (Time.timeSinceLevelLoad < 25f) return;        // wait for traffic
            try
            {
                var tcType = FindGameType("TaxiController");
                if (tcType == null)
                {
                    Plugin.Logger.LogWarning("[Taxi] TaxiController type not found.");
                    _taxiProbed = true;
                    return;
                }
                var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(tcType));
                if (arr == null || arr.Length == 0) return;   // no taxi yet — retry
                _taxiProbed = true;

                Plugin.Logger.LogInfo($"[Taxi] === Taxi probe ({arr.Length} taxi(s) in scene) ===");
                DumpTypeMembers("TaxiController");

                var comp = arr[0].TryCast<Component>();
                if (comp != null)
                {
                    var root = comp.transform.root;
                    Plugin.Logger.LogInfo($"[Taxi] Sample taxi '{root.name}' hierarchy:");
                    DumpNode(root, 0);
                    for (int i = 0; i < root.childCount && i < 16; i++)
                        DumpNode(root.GetChild(i), 1);
                }
                Plugin.Logger.LogInfo("[Taxi] === Taxi probe done ===");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Taxi] ProbeTaxi: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── Vehicle sync ──────────────────────────────────────────────────────
        //
        // Local side: enumerate every owned vehicle (VehicleHelper.AllPlayerVehicles)
        // and broadcast the whole fleet — parked and driven — at ~10 Hz.  The fleet
        // is the complete truth for that owner.
        // Remote side: each vehicle is a persistent "ghost" spawned with the real
        // game vehicle (CreateAndSpawnVehicle), kept out of our owned set, physics
        // frozen, interaction + ownership icon suppressed, an owner-name label
        // above it, driven purely by the synced transform.  A ghost is despawned
        // only when its vehicle drops out of the owner's fleet (sold) or the owner
        // disconnects.

        private sealed class RemoteVehicle
        {
            public string      OwnerId   = "";
            public GameObject? Go;
            public Transform?  Label;
            public Vector3     TargetPos;
            public Quaternion  TargetRot = Quaternion.identity;
            // Dead reckoning (same as traffic ghosts): chase a target
            // extrapolated along measured velocity so a driven car never
            // stutters between network updates at low FPS.
            public Vector3     Velocity;
            public float       TargetAt;   // receiver clock at arrival (extrapolation base)
            public float       SenderT;    // sender's sample clock (velocity measurement)
        }
        private const float MaxVehicleExtrapolateSeconds = 0.3f;
        private const float VehicleSnapDistance          = 15f;

        // Gameplay components destroyed on a ghost so the game stops treating it
        // as a vehicle anyone owns — no map icon, no tickets, not enterable.
        // Visual components (renderers, CarFeatures which holds the colour) stay.
        private static readonly string[] _killVehicleComponents =
        {
            "VehicleController", "CarController", "DamageHandler",
            "VehicleDeformationController", "WheelControllerManager",
            "FuelModuleWrapper", "SpeedLimiterModuleWrapper",
            "FlipOverModuleWrapper", "VariableCenterOfMass",
        };

        // Keyed by VehicleId (a player owns several vehicles).
        private static readonly Dictionary<string, RemoteVehicle> _remoteVehicles = new();

        /// <summary>Reads the local player's full vehicle fleet, or null if not ready.</summary>
        public static VehicleFleetPayload? ReadLocalFleet()
        {
            try
            {
                var list = VehicleHelper.AllPlayerVehicles;
                if (list == null) return null;

                Transform? openDriven = null;
                var fleet = new VehicleFleetPayload { OwnerId = MPConfig.PlayerId, T = Time.unscaledTime };
                for (int i = 0; i < list.Count; i++)
                {
                    var vc = list[i];
                    if (vc == null) continue;
                    var inst = vc.vehicleInstance;
                    if (inst == null || string.IsNullOrEmpty(inst.id)) continue;
                    var t = vc.transform;
                    string tn = inst.vehicleTypeName.ToString();
                    // Ground-truth capture: while WE drive/push, record the exact
                    // character-to-vehicle offset + animator state so the remote
                    // rendering can be modeled from data instead of guesses.
                    if (vc.controlledByPlayer)
                    {
                        MPRideProbe.Sample(tn, t);
                        if (IsOpenVehicle(tn)) openDriven = t;   // hand-IK sync anchor
                    }
                    fleet.Vehicles.Add(new VehicleEntry
                    {
                        VehicleId = inst.id,
                        TypeName  = tn,
                        ColorName = inst.vehicleColorName ?? "",
                        Driving   = vc.controlledByPlayer,
                        X = t.position.x, Y = t.position.y, Z = t.position.z,
                        Qx = t.rotation.x, Qy = t.rotation.y, Qz = t.rotation.z, Qw = t.rotation.w,
                    });
                }
                CurrentOpenDriven = openDriven;
                return fleet;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle] ReadLocalFleet: {ex.Message}");
                return null;
            }
        }

        /// <summary>The open vehicle WE are currently pushing/riding, if any —
        /// the vehicle-local anchor for the hand-IK sync (refreshed each fleet
        /// read; null when on foot or in an enclosed vehicle).</summary>
        public static Transform? CurrentOpenDriven;

        /// <summary>Lets the RideProbe capture in SINGLE-PLAYER too (the fleet
        /// reader that hosts the probe normally only runs while MP is active):
        /// read-and-discard the local fleet at 1 Hz.</summary>
        private static float _soloProbeNext;
        public static void ProbeOwnFleetSolo()
        {
            if (Time.unscaledTime < _soloProbeNext) return;
            _soloProbeNext = Time.unscaledTime + 1f;
            try { ReadLocalFleet(); } catch { }
        }

        /// <summary>Applies a remote player's full vehicle fleet (main thread).</summary>
        // CLAUDE-DIAGNOSTIC — kill-switch gate for owned-vehicle fleet sync.
        // When false, ApplyVehicleFleet returns early (no ghost vehicles
        // spawned for the host's owned cars).  Used by F4 master kill switch.
        public static bool ClientApplyFleetEnabled { get; set; } = true;

        public static void ApplyVehicleFleet(VehicleFleetPayload p)
        {
            if (p == null || string.IsNullOrEmpty(p.OwnerId)) return;
            if (SaveGameManager.Current == null) return;
            if (!ClientApplyFleetEnabled) return;     // CLAUDE-DIAGNOSTIC F4 gate
            try
            {
                var seen = new HashSet<string>();
                bool anyDriving = false;

                foreach (var e in p.Vehicles)
                {
                    if (string.IsNullOrEmpty(e.VehicleId)) continue;
                    seen.Add(e.VehicleId);
                    if (e.Driving) anyDriving = true;

                    var pos = new Vector3(e.X, e.Y, e.Z);
                    var rot = new Quaternion(e.Qx, e.Qy, e.Qz, e.Qw);

                    if (!_remoteVehicles.TryGetValue(e.VehicleId, out var rv) || rv.Go == null)
                    {
                        var spawned = SpawnRemoteVehicle(p.OwnerId, e, pos, rot);
                        if (spawned == null) continue;
                        rv = spawned;
                        rv.TargetAt = Time.unscaledTime;
                        _remoteVehicles[e.VehicleId] = rv;
                    }
                    if (rv.Go != null && Vector3.Distance(rv.Go.transform.position, pos) > VehicleSnapDistance)
                    {
                        // Teleport (ferry, recovery) — snap, don't slide.
                        rv.Go.transform.position = pos;
                        rv.Go.transform.rotation = rot;
                        rv.Velocity = Vector3.zero;
                    }
                    else
                    {
                        // Velocity from the SENDER's clock (packet stamp); tiny
                        // delta = two packets in one frame → keep prior velocity.
                        float vdt = p.T - rv.SenderT;
                        if (vdt > 0.005f)
                            rv.Velocity = (pos - rv.TargetPos) / vdt;
                    }
                    rv.TargetPos = pos;
                    rv.TargetRot = rot;
                    rv.TargetAt  = Time.unscaledTime;
                    rv.SenderT   = p.T;
                }

                // Vehicles this owner no longer lists have been sold — despawn them.
                var stale = _remoteVehicles
                    .Where(kv => kv.Value.OwnerId == p.OwnerId && !seen.Contains(kv.Key))
                    .Select(kv => kv.Key).ToList();
                foreach (var id in stale) DespawnByVehicleId(id);

                // Hide the owner's walking model only when driving an ENCLOSED
                // vehicle.  On OPEN ones (scooters, push carts/dollies) the
                // rider/pusher must stay visible — hiding them made carts roll
                // around with nobody pushing (user report 2026-06-10).  The
                // avatar's own position sync keeps it at the vehicle.
                bool hideAvatar = false;
                Transform? ride = null;
                Vector3 rideOff = Vector3.zero;
                foreach (var e in p.Vehicles)
                {
                    if (!e.Driving) continue;
                    if (IsOpenVehicle(e.TypeName))
                    {
                        if (_openVehicleLogged.Add(e.TypeName))
                            Plugin.Logger.LogInfo($"[Vehicle] '{e.TypeName}' driven — OPEN vehicle, avatar stays visible.");
                        // Pin the avatar to the vehicle ghost (separately smoothed
                        // streams drift apart — the pusher floated off the cart).
                        if (_remoteVehicles.TryGetValue(e.VehicleId, out var rvm) && rvm.Go != null)
                        {
                            ride    = rvm.Go.transform;
                            rideOff = RideOffsetFor(e.TypeName);
                        }
                        else if (_openVehicleLogged.Add(e.TypeName + ":noghost"))
                        {
                            Plugin.Logger.LogWarning($"[RideDiag] open '{e.TypeName}' driven but its ghost is NOT in _remoteVehicles — ride pin INACTIVE (id={e.VehicleId}).");
                        }
                    }
                    else
                    {
                        hideAvatar = true;
                        if (_openVehicleLogged.Add(e.TypeName))
                            Plugin.Logger.LogInfo($"[Vehicle] '{e.TypeName}' driven — enclosed, avatar hidden (add to OpenVehicleNames if wrong).");
                    }
                }
                RemotePlayerManager.SetDriving(p.OwnerId, hideAvatar);
                RemotePlayerManager.SetRide(p.OwnerId, ride, rideOff);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] ApplyVehicleFleet '{p.OwnerId}': {ex.Message}");
            }
        }

        // Vehicle types the player rides ON / pushes (avatar stays visible).
        // Matched as substrings of VehicleTypeName — every driven type gets
        // logged once so this list can be refined from real data.
        private static readonly string[] OpenVehicleNames =
            { "scooter", "cart", "dolly", "dollie", "trolley", "bike", "moped", "pallet", "hand",
              "flatbed", "barrow", "wagon" };   // 'Flatbed' confirmed from the driven-type log 2026-06-10
        private static readonly HashSet<string> _openVehicleLogged = new();

        private static bool IsOpenVehicle(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            foreach (var k in OpenVehicleNames)
                if (typeName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Where the rider/pusher stands relative to the vehicle ghost.
        /// Geometry-probe-MEASURED (2026-06-10): the character TRANSFORM sits at
        /// the vehicle transform, but the game's pushing system RENDERS the
        /// body 1.00m behind it (sender bodyInVeh z=-1.08 vs observer -0.08,
        /// cart visuals identical).  That system is script-driven and absent on
        /// clones, so the pin reproduces it: pushables -1.0m; rideables stand
        /// on the deck at the root (scooter verified fine at zero).</summary>
        private static Vector3 RideOffsetFor(string typeName)
        {
            bool rideOn = typeName.IndexOf("scooter", StringComparison.OrdinalIgnoreCase) >= 0
                       || typeName.IndexOf("bike",    StringComparison.OrdinalIgnoreCase) >= 0
                       || typeName.IndexOf("moped",   StringComparison.OrdinalIgnoreCase) >= 0;
            return rideOn ? Vector3.zero : new Vector3(0f, 0f, -1.0f);
        }

        private static RemoteVehicle? SpawnRemoteVehicle(string ownerId, VehicleEntry e,
                                                         Vector3 pos, Quaternion rot)
        {
            VehicleInstance inst;
            try
            {
                inst = new VehicleInstance();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] new VehicleInstance failed: {ex.Message}");
                return null;
            }
            try
            {
                inst.id               = "BAMP_" + e.VehicleId;
                inst.vehicleColorName = e.ColorName;
                inst.vehicleTypeName  = (VehicleTypeName)Enum.Parse(typeof(VehicleTypeName), e.TypeName);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle] VehicleInstance setup ('{e.TypeName}'): {ex.Message}");
            }

            int ownedBefore = SafeCount(() => VehicleHelper.AllPlayerVehicles?.Count ?? -1);

            VehicleController? vc;
            try
            {
                vc = VehicleHelper.CreateAndSpawnVehicle(inst, pos, rot);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] CreateAndSpawnVehicle failed: {ex.Message}");
                return null;
            }
            if (vc == null)
            {
                Plugin.Logger.LogWarning("[Vehicle] CreateAndSpawnVehicle returned null.");
                return null;
            }

            var go  = vc.gameObject;
            int ownedAfterSpawn = SafeCount(() => VehicleHelper.AllPlayerVehicles?.Count ?? -1);
            string poiName;
            try { poiName = vc.poi != null ? vc.poi.gameObject.name : "(null)"; }
            catch { poiName = "(error)"; }

            // De-register from the player vehicle system.
            try { VehicleHelper.UnregisterPlayerVehicle(vc); } catch { }
            // Destroy the point-of-interest (map/world "owned" icon).
            try { if (vc.poi != null) UnityEngine.Object.Destroy(vc.poi.gameObject); } catch { }
            // Destroy every gameplay component — after this the game no longer
            // sees a vehicle here, just a prop: no ownership, no ticket, no entry.
            int killed = StripVehicleComponents(go);
            // Freeze physics — we drive the ghost purely by the synced transform.
            try
            {
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
            }
            catch { }

            int ownedAfterStrip = SafeCount(() => VehicleHelper.AllPlayerVehicles?.Count ?? -1);
            Plugin.Logger.LogInfo(
                $"[Vehicle] Ghost '{e.TypeName}' for '{ownerId}': AllPlayerVehicles " +
                $"{ownedBefore}→{ownedAfterSpawn}→{ownedAfterStrip}, poi='{poiName}', " +
                $"{killed} gameplay component(s) destroyed.");

            var label = CreateOwnerLabel(go, ownerId);
            return new RemoteVehicle
            {
                OwnerId   = ownerId,
                Go        = go,
                Label     = label,
                TargetPos = pos,
                TargetRot = rot,
            };
        }

        /// <summary>
        /// Spawns a pure-visual vehicle ghost of the given model: a real game
        /// vehicle, immediately demoted to an inert prop (de-registered, POI
        /// destroyed, gameplay components stripped, physics frozen).  Used for
        /// both remote-player vehicles and AI-traffic ghosts.  Returns the
        /// GameObject, or null on failure.
        /// </summary>
        public static GameObject? SpawnVisualGhost(string typeName, Vector3 pos, Quaternion rot)
        {
            VehicleInstance inst;
            try { inst = new VehicleInstance(); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] ghost VehicleInstance failed: {ex.Message}");
                return null;
            }
            try
            {
                inst.id = "BAMP_ghost";
                if (Enum.TryParse<VehicleTypeName>(typeName, out var vtn))
                    inst.vehicleTypeName = vtn;
                else
                    inst.vehicleTypeName = VehicleTypeName.VordV150;   // fallback (e.g. Taxi)
            }
            catch { }

            VehicleController? vc;
            try { vc = VehicleHelper.CreateAndSpawnVehicle(inst, pos, rot); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] ghost CreateAndSpawnVehicle failed: {ex.Message}");
                return null;
            }
            if (vc == null) return null;

            var go = vc.gameObject;
            try { VehicleHelper.UnregisterPlayerVehicle(vc); } catch { }
            try { if (vc.poi != null) UnityEngine.Object.Destroy(vc.poi.gameObject); } catch { }
            StripVehicleComponents(go);
            try
            {
                // EVERY rigidbody in the hierarchy, not just the root — a dynamic
                // child rb lets the local player physically shove the ghost.
                var rbs = go.GetComponentsInChildren(Il2CppType.Of<Rigidbody>(), true);
                for (int i = 0; i < rbs.Length; i++)
                {
                    var rb = rbs[i].TryCast<Rigidbody>();
                    if (rb == null) continue;
                    rb.isKinematic = true;
                    rb.useGravity  = false;
                }
            }
            catch { }
            return go;
        }

        /// <summary>Destroy any Camera hiding in a cloned ghost's hierarchy — a
        /// stowaway ENABLED camera can hijack Camera.main, making the game's
        /// cursor pick-ray fire from the wrong eye (buildings highlight away
        /// from the cursor — 2026-06-10 investigation).</summary>
        public static int StripCameras(GameObject go)
        {
            int n = 0;
            try
            {
                var cams = go.GetComponentsInChildren(Il2CppType.Of<Camera>(), true);
                for (int i = 0; i < cams.Length; i++)
                {
                    var c = cams[i].TryCast<Camera>();
                    if (c == null) continue;
                    UnityEngine.Object.Destroy(c);
                    n++;
                }
                if (n > 0)
                    Plugin.Logger.LogWarning($"[Ghost] STRIPPED {n} stowaway camera(s) from clone '{go.name}' — these hijack the cursor pick ray!");
            }
            catch { }
            return n;
        }

        /// <summary>Destroys the gameplay components that make a GameObject "a vehicle".</summary>
        private static int StripVehicleComponents(GameObject go)
        {
            int n = 0;
            StripCameras(go);
            try
            {
                var comps = go.GetComponents(Il2CppType.Of<Component>());
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    if (System.Array.IndexOf(_killVehicleComponents, c.GetIl2CppType().Name) >= 0)
                    {
                        UnityEngine.Object.Destroy(c);
                        n++;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle] StripVehicleComponents: {ex.Message}");
            }
            return n;
        }

        private static int SafeCount(Func<int> f)
        {
            try { return f(); } catch { return -1; }
        }

        /// <summary>Creates a world-space owner-name label above a ghost vehicle.</summary>
        private static Transform? CreateOwnerLabel(GameObject vehicle, string ownerId)
        {
            try
            {
                var labelGO = new GameObject("BAMP_VehicleLabel");
                labelGO.transform.SetParent(vehicle.transform, false);
                labelGO.transform.localPosition = new Vector3(0f, 2.4f, 0f);

                var canvas = labelGO.AddComponent<Canvas>();
                canvas.renderMode  = RenderMode.WorldSpace;
                canvas.sortingOrder = 10;

                var rt = labelGO.GetComponent<RectTransform>();
                rt.sizeDelta  = new Vector2(300f, 60f);
                rt.localScale = new Vector3(0.01f, 0.01f, 0.01f);

                var tmp = labelGO.AddComponent<TextMeshProUGUI>();
                tmp.text      = ownerId + "'s car";
                tmp.fontSize  = 42f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color     = new Color(1f, 0.85f, 0.4f);
                return labelGO.transform;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle] CreateOwnerLabel: {ex.Message}");
                return null;
            }
        }

        /// <summary>Despawns one ghost by vehicle id.</summary>
        public static void DespawnByVehicleId(string vehicleId)
        {
            if (_remoteVehicles.TryGetValue(vehicleId, out var rv))
            {
                if (rv.Go != null) { try { UnityEngine.Object.Destroy(rv.Go); } catch { } }
                _remoteVehicles.Remove(vehicleId);
                Plugin.Logger.LogInfo($"[Vehicle] Despawned ghost '{vehicleId}'");
            }
        }

        /// <summary>Despawns every ghost vehicle owned by a player (they disconnected).</summary>
        public static void DespawnAllOwnedBy(string ownerId)
        {
            var ids = _remoteVehicles.Where(kv => kv.Value.OwnerId == ownerId)
                                     .Select(kv => kv.Key).ToList();
            foreach (var id in ids) DespawnByVehicleId(id);
        }

        /// <summary>Despawns every ghost vehicle (disconnect / scene unload).</summary>
        public static void DespawnAll()
        {
            foreach (var rv in _remoteVehicles.Values)
                if (rv.Go != null) { try { UnityEngine.Object.Destroy(rv.Go); } catch { } }
            _remoteVehicles.Clear();
        }

        /// <summary>Smooths ghost vehicles toward their networked transform; billboards labels.</summary>
        public static void TickSmoothing()
        {
            if (_remoteVehicles.Count == 0) return;
            // Capped so packet corrections blend over frames at low FPS.
            float k   = Mathf.Min(Time.deltaTime * 12f, 0.5f);
            float now = Time.unscaledTime;
            var cam = Camera.main;
            foreach (var rv in _remoteVehicles.Values)
            {
                if (rv.Go == null) continue;
                var t = rv.Go.transform;
                // Chase the dead-reckoned point (target + velocity·elapsed) so
                // motion stays continuous between packets even at low FPS.
                float ahead = Mathf.Min(now - rv.TargetAt, MaxVehicleExtrapolateSeconds);
                var predicted = rv.TargetPos + rv.Velocity * ahead;
                t.position = Vector3.Lerp(t.position, predicted, k);
                t.rotation = Quaternion.Slerp(t.rotation, rv.TargetRot, k);
                if (rv.Label != null && cam != null)
                    rv.Label.rotation = cam.transform.rotation;
            }
        }

        // ── AI traffic discovery probe ────────────────────────────────────────
        //
        // Phase 5 prep.  Discovers the world-traffic system: which classes drive
        // AI traffic, how many traffic vehicles are live, what components they
        // carry, and the AI driver/spawner API — the data needed to design
        // host-authoritative traffic sync.  Logging only; zero side effects.

        private static bool _trafficTypesScanned;
        private static bool _trafficSceneScanned;

        private static readonly string[] _trafficKeywords =
        {
            "traffic", "aicar", "aivehicle", "aidriver", "npccar", "npcvehicle", "carspawn"
        };

        private static bool _gleyApiScanned;

        private static bool _gleyInternalsScanned;

        public static void ProbeTraffic()
        {
            try
            {
                ScanTrafficTypes();      // once — the traffic class landscape
                ScanSceneTraffic();      // once, after traffic has had time to spawn
                ScanGleyApi();           // once — the GleyTrafficSystem class APIs
                ScanGleyInternals();     // once — per-vehicle / pool / spawn-anchor APIs
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Traffic] ProbeTraffic: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // Dumps the Gley internals needed to design host-authoritative sync:
        // the per-vehicle component, the pool, the vehicle-type enum, and the
        // spawn/density/positioning system (the citywide-anchor question).
        private static void ScanGleyInternals()
        {
            if (_gleyInternalsScanned) return;
            if (SaveGameManager.Current == null) return;
            if (Time.timeSinceLevelLoad < 25f) return;
            _gleyInternalsScanned = true;

            Plugin.Logger.LogInfo("[Traffic] === GleyTrafficSystem internals dump ===");
            foreach (var name in new[]
            {
                "GleyTrafficSystem.VehicleComponent",
                "GleyTrafficSystem.VehiclePool",
                "GleyTrafficSystem.VehicleTypes",
                "GleyTrafficSystem.DensityManager",
                "GleyTrafficSystem.VehiclePositioningSystem",
                "GleyTrafficSystem.WaypointManager",
                "GleyTrafficSystem.DrivingAI",
            })
                DumpTypeMembers(name);
            Plugin.Logger.LogInfo("[Traffic] === GleyTrafficSystem internals dump done ===");
        }

        // Dumps the GleyTrafficSystem APIs + a sample live AI traffic car.
        private static void ScanGleyApi()
        {
            if (_gleyApiScanned) return;
            if (SaveGameManager.Current == null) return;
            if (Time.timeSinceLevelLoad < 25f) return;     // wait for traffic
            _gleyApiScanned = true;

            Plugin.Logger.LogInfo("[Traffic] === GleyTrafficSystem API dump ===");
            foreach (var name in new[]
            {
                "GleyTrafficSystem.TrafficManager",
                "GleyTrafficSystem.API",
                "GleyTrafficSystem.TrafficVehicles",
                "GleyTrafficSystem.TrafficComponent",
                "GleyTrafficSystem.TrafficComponentMultiplayer",
                "AiCarHorn",
                "TrafficDespawner",
            })
                DumpTypeMembers(name);

            // Sample one live AI traffic car (found via its AiCarHorn component).
            try
            {
                var hornT = FindGameType("AiCarHorn");
                if (hornT != null)
                {
                    var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(hornT));
                    if (arr != null && arr.Length > 0)
                    {
                        var comp = arr[0].TryCast<Component>();
                        if (comp != null)
                        {
                            var root = comp.transform.root;
                            Plugin.Logger.LogInfo($"[Traffic] Sample AI car '{root.name}':");
                            DumpNode(root, 0);
                            for (int i = 0; i < root.childCount && i < 12; i++)
                                DumpNode(root.GetChild(i), 1);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Traffic] AI car sample: {ex.Message}");
            }
            Plugin.Logger.LogInfo("[Traffic] === GleyTrafficSystem API dump done ===");
        }

        private static void ScanTrafficTypes()
        {
            if (_trafficTypesScanned) return;
            if (PlayerHelper.PlayerController == null) return;
            _trafficTypesScanned = true;

            Plugin.Logger.LogInfo("[Traffic] === Traffic-type assembly scan ===");
            int found = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.Name == null) continue;
                    string lo = t.Name.ToLowerInvariant();
                    bool hit = false;
                    foreach (var kw in _trafficKeywords)
                        if (lo.Contains(kw)) { hit = true; break; }
                    if (hit) { Plugin.Logger.LogInfo($"[Traffic]   {t.FullName}"); found++; }
                }
            }
            Plugin.Logger.LogInfo($"[Traffic] === {found} traffic-named type(s) ===");
        }

        private static void ScanSceneTraffic()
        {
            if (_trafficSceneScanned) return;
            if (SaveGameManager.Current == null) return;
            // Give the world traffic time to populate before sampling it.
            if (Time.timeSinceLevelLoad < 25f) return;
            _trafficSceneScanned = true;

            Plugin.Logger.LogInfo("[Traffic] === Scene traffic scan ===");

            // Owned-vehicle instance ids (to separate player cars from AI traffic)
            var ownedIds = new HashSet<int>();
            try
            {
                var owned = VehicleHelper.AllPlayerVehicles;
                if (owned != null)
                    for (int i = 0; i < owned.Count; i++)
                        if (owned[i] != null) ownedIds.Add(owned[i].GetInstanceID());
            }
            catch { }

            // All VehicleControllers in the scene — classify owned vs AI/other.
            VehicleController? sample = null;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<VehicleController>());
                int player = 0, ai = 0;
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var vc = all[i].TryCast<VehicleController>();
                        if (vc == null) continue;
                        if (ownedIds.Contains(vc.GetInstanceID())) { player++; }
                        else { ai++; if (sample == null) sample = vc; }
                    }
                }
                Plugin.Logger.LogInfo(
                    $"[Traffic] VehicleControllers in scene: {(all != null ? all.Length : 0)} " +
                    $"— {player} player-owned, {ai} AI/other");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Traffic] VehicleController scan: {ex.Message}");
            }

            // Count other candidate traffic-vehicle component types.
            foreach (var name in new[] { "CarController", "AiCarHorn", "AiCarMusic", "Pedestrian" })
                CountObjectsOfType(name);

            // Dump a sample AI vehicle: hierarchy + AI-ish component APIs.
            if (sample != null)
            {
                Plugin.Logger.LogInfo($"[Traffic] Sample AI vehicle '{sample.gameObject.name}':");
                DumpNode(sample.transform, 0);
                try
                {
                    var comps = sample.gameObject.GetComponents(Il2CppType.Of<Component>());
                    var dumped = new HashSet<string>();
                    for (int i = 0; i < comps.Length; i++)
                    {
                        if (comps[i] == null) continue;
                        string cn = comps[i].GetIl2CppType().Name;
                        string lo = cn.ToLowerInvariant();
                        if ((lo.Contains("ai") || lo.Contains("driver") ||
                             lo.Contains("traffic") || lo.Contains("nav")) && dumped.Add(cn))
                            DumpMembers(cn, comps[i]);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[Traffic] sample component dump: {ex.Message}");
                }
            }
            Plugin.Logger.LogInfo("[Traffic] === Scene traffic scan done ===");
        }

        private static void CountObjectsOfType(string typeName)
        {
            try
            {
                var t = FindGameType(typeName);
                if (t == null)
                {
                    Plugin.Logger.LogInfo($"[Traffic]   {typeName}: type not found");
                    return;
                }
                var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(t));
                Plugin.Logger.LogInfo($"[Traffic]   {typeName}: {(arr != null ? arr.Length : 0)} in scene");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Traffic]   {typeName}: {ex.Message}");
            }
        }
    }
}
