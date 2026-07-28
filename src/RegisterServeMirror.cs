using System;
using HarmonyLib;
using UnityEngine;
using BigAmbitions.Characters;    // Gender

namespace BigAmbitionsMP
{
    /// <summary>Round-119 — SIMULATOR SIDE of the serve mirror.  Emits one beat when a customer's serve starts
    /// and one when it finishes, so a player working that till on another machine can perform the job in step.
    ///
    /// WHY THIS EXISTS: working a register is not a special player mode — WorkActivity.StartWorking assigns the
    /// PLAYER'S OWN CHARACTER as the station's employee, and the station attaches the same employee component an
    /// AI would get, so the ordinary serve loop runs on their body (fetch each item with a 2.5s animation,
    /// return, the customer animates, a gendered sound plays).  A follower cannot run any of that: their machine
    /// has no real Customers, only puppets.  Their till IS being served — by a stand-in on this machine — and
    /// those customers stream back to them, so they watch a queue being worked while standing perfectly still.
    ///
    /// ONLY START AND FINISH ARE MIRRORED.  The beats in between are synthesised on the follower.  Mirroring
    /// every internal beat would need the employee's whole path (that is option 3, rejected: it makes a player's
    /// own avatar a puppet of a remote AI).  Bounding the local performance by two real events means latency can
    /// shift it slightly but can never leave it playing after the real serve ended.</summary>
    internal static class RegisterServeMirror
    {
        private static void Emit(FullServiceEmployee emp, bool finished)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                if (emp == null) return;

                var station = emp.employeeStationController;
                var cust    = emp.customer;
                if (station == null || cust == null) return;

                string addr = MPRegisterSync.CurrentShopAddress ?? "";
                if (string.IsNullOrEmpty(addr)) return;
                if (!CustomerPuppets.IAmSimulatorFor(addr)) return;   // only the machine running the customers speaks

                string custId = CustomerPuppets.RowIdForCustomer(cust);
                if (string.IsNullOrEmpty(custId)) return;

                bool male = true;
                try { male = cust.tpc == null || cust.tpc.appearanceSetter.data.gender == Gender.Male; } catch { }

                var p = new RegisterServePayload
                {
                    AddressKey   = addr,
                    SimulatorPid = MPConfig.PlayerId,
                    StationKey   = MPRegisterSync.StationKeyOf(station.transform.position),
                    CustomerId   = custId,
                    Finished     = finished,
                    Male         = male,
                };

                if (MPServer.IsRunning) MPServer.BroadcastRegisterServe(p);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.RegisterServe, MPConfig.PlayerId, p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] serve beat: {ex.Message}"); }
        }

        /// <summary>Serve START.  ServeCustomer is a coroutine, so this Prefix fires as the state machine is
        /// created — i.e. exactly when serving begins, with `customer` and `employeeStationController` already
        /// set on the employee.</summary>
        [HarmonyPatch(typeof(FullServiceEmployee), "ServeCustomer")]
        public static class Patch_FullServiceEmployee_ServeCustomer_Beat
        {
            static void Prefix(FullServiceEmployee __instance) => Emit(__instance, finished: false);
        }

        /// <summary>Serve FINISH.  Read the customer BEFORE the native body runs — FinishServingCustomer marks
        /// them served and then nulls the field, so a Postfix would have nothing left to name.</summary>
        [HarmonyPatch(typeof(FullServiceEmployee), "FinishServingCustomer")]
        public static class Patch_FullServiceEmployee_FinishServing_Beat
        {
            static void Prefix(FullServiceEmployee __instance) => Emit(__instance, finished: true);
        }
    }
}
