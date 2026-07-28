using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using BigAmbitions.Characters;    // Gender

namespace BigAmbitionsMP
{
    /// <summary>Round-119/146 — SIMULATOR SIDE of the serve mirror.  Streams the stand-in's REAL serve
    /// events so a player working that till on another machine performs the same actions the native serve
    /// performs for THAT shop type (field req 2026-07-28: "a system that matches the behavior of that shop"):
    ///
    ///   - FullServiceEmployee (fast food, ...): start → one FETCH beat per grabbed item, carrying the real
    ///     item position (GrabItem is the single native funnel) → end (walk home + ring-up on the receiver).
    ///   - SelfServiceEmployee (checkout shops): a single start beat flagged SelfService with the native
    ///     ring-up duration (UsingCashRegister, 2s full-service-till / 3s otherwise) — no walking, matching
    ///     the "only a ring-up" businesses.  Its serve is one inline coroutine with no separate finish, so
    ///     no end beat is emitted (the receiver's performance is fully described by the start beat).
    ///
    /// A cancelled serve emits an end beat marked Cancelled so the receiver resets without a ring-up.
    /// Receivers filter by station key, so beats for other tills (hired AI staff) are ignored there.</summary>
    internal static class RegisterServeMirror
    {
        /// <summary>Serves in flight, keyed by the body doing them — GrabItem only knows the body.</summary>
        private static readonly Dictionary<ThirdPersonCharacter, Employee> _serving = new();

        private static void Emit(Employee emp, int kind, bool selfService = false, float dur = 0f,
                                 Vector3? fetch = null, bool cancelled = false)
        {
            int entries = 0;
            try { entries = emp?.customer?.order?.entries?.Count ?? 0; } catch { }
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                if (emp == null) return;

                var station = emp.employeeStationController;
                var cust    = emp.customer;
                if (station == null) return;
                if (cust == null && !cancelled) return;   // cancels may fire after the customer is gone

                string addr = MPRegisterSync.CurrentShopAddress ?? "";
                if (string.IsNullOrEmpty(addr)) return;
                if (!CustomerPuppets.IAmSimulatorFor(addr)) return;   // only the machine running the customers speaks

                string custId = cust != null ? CustomerPuppets.RowIdForCustomer(cust) : "";
                bool male = true;
                try { male = cust == null || cust.tpc == null || cust.tpc.appearanceSetter.data.gender == Gender.Male; } catch { }

                var f = fetch ?? Vector3.zero;
                var p = new RegisterServePayload
                {
                    AddressKey   = addr,
                    SimulatorPid = MPConfig.PlayerId,
                    StationKey   = MPRegisterSync.StationKeyOf(station.transform.position),
                    CustomerId   = custId,
                    Kind         = kind,
                    Finished     = kind == 2,
                    SelfService  = selfService,
                    Cancelled    = cancelled,
                    Dur          = dur,
                    Male         = male,
                    Entries      = entries,
                    FX = f.x, FY = f.y, FZ = f.z,
                };

                if (MPServer.IsRunning) MPServer.BroadcastRegisterServe(p);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.RegisterServe, MPConfig.PlayerId, p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] serve beat: {ex.Message}"); }
        }

        /// <summary>Full-service serve START — register the body so fetch beats can name their serve.</summary>
        [HarmonyPatch(typeof(FullServiceEmployee), "ServeCustomer")]
        public static class Patch_FullServiceEmployee_ServeCustomer_Beat
        {
            static void Prefix(FullServiceEmployee __instance)
            {
                try { if (__instance.employeeTpc != null) _serving[__instance.employeeTpc] = __instance; } catch { }
                Emit(__instance, kind: 0);
            }
        }

        /// <summary>One beat per grabbed item, with the REAL item position — the native fetch funnel.
        /// Static method: the body parameter is the only identity, hence the _serving map.</summary>
        [HarmonyPatch(typeof(FullServiceEmployee), "GrabItem")]
        public static class Patch_FullServiceEmployee_GrabItem_Beat
        {
            static void Prefix(ItemController itemController, ThirdPersonCharacter employeeTpc)
            {
                try
                {
                    if (itemController == null || employeeTpc == null) return;
                    if (_serving.TryGetValue(employeeTpc, out var emp))
                        Emit(emp, kind: 1, fetch: itemController.transform.position);
                }
                catch { }
            }
        }

        /// <summary>Serve FINISH.  Prefix — the native body nulls `customer` before a Postfix could read it.</summary>
        [HarmonyPatch(typeof(FullServiceEmployee), "FinishServingCustomer")]
        public static class Patch_FullServiceEmployee_FinishServing_Beat
        {
            static void Prefix(FullServiceEmployee __instance)
            {
                Emit(__instance, kind: 2);
                try { if (__instance.employeeTpc != null) _serving.Remove(__instance.employeeTpc); } catch { }
            }
        }

        /// <summary>Aborted serve — reset the receiver (walk home, no ring-up).</summary>
        [HarmonyPatch(typeof(FullServiceEmployee), "CancelCurrentOrder")]
        public static class Patch_FullServiceEmployee_Cancel_Beat
        {
            static void Prefix(FullServiceEmployee __instance)
            {
                Emit(__instance, kind: 2, cancelled: true);
                try { if (__instance.employeeTpc != null) _serving.Remove(__instance.employeeTpc); } catch { }
            }
        }

        /// <summary>Self-service serve — one beat carrying the native ring-up duration (the same tag-dependent
        /// expression the native coroutine uses).  No fetch, no end beat: that IS the whole performance.</summary>
        [HarmonyPatch(typeof(SelfServiceEmployee), "ServeCustomer")]
        public static class Patch_SelfServiceEmployee_ServeCustomer_Beat
        {
            static void Prefix(SelfServiceEmployee __instance)
            {
                float dur = 3f;
                try
                {
                    var item = __instance.employeeStationController?.Item;
                    if (item != null && item.HasTag(BigAmbitions.Tags.TagRef.Itemtag.isfullservicecashregister)) dur = 2f;
                }
                catch { }
                Emit(__instance, kind: 0, selfService: true, dur: dur);
            }
        }
    }
}
