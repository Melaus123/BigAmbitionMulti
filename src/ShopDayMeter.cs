using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>B5 (batch 26 fold 2) - THE MEASUREMENT BEHIND THE LIVE-vs-PAPER TABLE. Plain int
    /// counters bumped on events that were already happening (a body spawns, a shopper completes an
    /// order, a shopper leaves, the mod's paper sim runs an hour). NOTHING PER FRAME, no registry
    /// sweeps, no allocation. Read and reset by the DEV lever `shopday`.
    ///
    /// SCOPE, and it matters when reading the numbers: the live events fire for bodies in whatever
    /// interior the LOCAL player is standing in, so a figure is only meaningful while that player
    /// stays in the measured shop for the whole window - which is exactly how the t-shopday-*
    /// scenarios are built. The paper counters carry the shop name they were recorded for.</summary>
    internal static class ShopDayMeter
    {
        /// <summary>Bodies the native spawner actually created (every spawn, skipping or not).</summary>
        internal static int BodiesSpawned;
        /// <summary>Customer.CompleteOrder calls - a shopper finishing their own checkout LIVE.
        /// The employee-served routes (full service, self-service station, kiosk) go through
        /// Order.Pay instead and are already counted per address by the `tilldupes` lever's
        /// liveCompleted field (Patch_Order_Pay_HelperForward.LivePayCount) - the two together are
        /// the live till adds.</summary>
        internal static int LiveCompleteOrders;
        /// <summary>Customer.Leave entered with the shopper's order NOT completed - a body that went
        /// home without paying (closing time, a failed serve, a release).</summary>
        internal static int UnpaidExits;
        /// <summary>Body-less entries the mod's paper sim was handed, and the orders it actually put
        /// on the till. The difference is the native hourly capacity drop.</summary>
        internal static int PaperCandidates, PaperOrders, PaperHours;
        internal static int CapDrops => PaperCandidates - PaperOrders < 0 ? 0 : PaperCandidates - PaperOrders;
        /// <summary>Most bodies seen standing in the local interior at any sampled moment.</summary>
        internal static int MaxBodies;
        /// <summary>The shop the paper counters belong to (blank if the sim never ran).</summary>
        internal static string PaperShop = "";

        /// <summary>BATCH-26 FOLD 3 (G) - THE HOURS THE PAPER SIMULATOR ACTUALLY COVERED, as day*24+hour.
        /// This is what makes the accounting identity answerable. Native RunHourly EXEMPTS the shop the
        /// local player is standing in (BusinessSimulatorHelper.cs:32), and the mod's own hourly sim only
        /// steps in while a skip is running or for the partial hour a skip ended in. So a NORMAL-SPEED
        /// hour spent standing in your own shop is billed by nobody but the live checkouts - vanilla
        /// behaves exactly the same way - and an entry of such an hour that never got a body is not this
        /// fold's unbilled shopper. Entries of those hours are reported separately (idNoPass) instead of
        /// being counted as the bug.</summary>
        private static readonly HashSet<int> _passHours = new HashSet<int>();
        internal static bool PaperCoveredHour(int day, int hour)
        {
            try { return _passHours.Contains(day * 24 + hour); } catch { return false; }
        }
        /// <summary>Game day/hour at the last `shopday reset` - the window these counters cover.</summary>
        internal static int WindowDay = -1, WindowHour = -1;

        // ── BATCH-26 FOLD 3 (D): THE MEASUREMENTS THAT REPLACE THE USER'S EYES ──
        /// <summary>VISIT LENGTH in GAME MINUTES, for bodies that both arrived and left inside the
        /// window. The clock is the game's own (TimeHelper.NowInMinutes, :61-66), so the figure is
        /// directly comparable between a normal-speed leg and a 50x one: a sped-up shop should show
        /// the SAME visit in game-minutes, only taking less real time.</summary>
        internal static float VisitSum, VisitMax;
        internal static int VisitN;
        /// <summary>Arrival game-minute per body on the floor. A body that never reaches Leave (pooled
        /// away, world unloaded) simply leaves a stale row; the table is cleared with the window and
        /// dropped wholesale if it ever grows past a shop-full of bodies.</summary>
        private static readonly Dictionary<Customer, float> _arrived =
            new Dictionary<Customer, float>(new TillDupes.RefEq<Customer>());

        /// <summary>FOLLOWER PUPPET LAG: how far a puppet stands from the target row that has just
        /// arrived for it, measured at the moment the row is applied. Horizontal distance in metres.
        /// A puppet that keeps up reads near zero; a puppet being dragged behind a 50x body does not.</summary>
        internal static float LagSum, LagMax;
        internal static int LagN;

        internal static void Reset()
        {
            BodiesSpawned = LiveCompleteOrders = UnpaidExits = 0;
            PaperCandidates = PaperOrders = PaperHours = MaxBodies = 0;
            PaperShop = "";
            VisitSum = VisitMax = 0f; VisitN = 0;
            try { _passHours.Clear(); } catch { }
            LagSum = LagMax = 0f; LagN = 0;
            try { _arrived.Clear(); } catch { }
            try { SkipHandback.ClearCounters(); } catch { }
            try { CustomerPuppets.ResetChurnTotals(); } catch { }
            WindowDay = WindowHour = -1;
            try { WindowDay = SaveGameManager.Current.Day; WindowHour = SaveGameManager.Current.Hour; } catch { }
        }

        internal static void NoteLag(float metres)
        {
            try
            {
                if (metres < 0f || metres > 500f) return;   // a teleport/rebuild is not lag
                LagSum += metres; LagN++;
                if (metres > LagMax) LagMax = metres;
            }
            catch { }
        }

        /// <summary>Cheap: one list-count read on an event that already happened.</summary>
        internal static void SampleBodies()
        {
            try
            {
                var l = IndoorCustomerSpawner.Customers;
                if (l != null && l.Count > MaxBodies) MaxBodies = l.Count;
            }
            catch { }
        }

        internal static void NoteBodySpawned(Customer? body)
        {
            try
            {
                BodiesSpawned++; SampleBodies();
                if (body == null) return;
                if (_arrived.Count > 512) _arrived.Clear();   // bodies are pooled: never let stale rows pile up
                _arrived[body] = TimeHelper.NowInMinutes();
            }
            catch { }
        }

        /// <summary>A body reaching Leave: if we saw it arrive, that is one complete visit.</summary>
        internal static void NoteBodyLeaving(Customer body)
        {
            try
            {
                if (body == null) return;
                float at;
                if (!_arrived.TryGetValue(body, out at)) return;
                _arrived.Remove(body);
                float mins = TimeHelper.NowInMinutes() - at;
                if (mins < 0f || mins > 1440f) return;       // a day roll under the body: not a visit length
                VisitSum += mins; VisitN++;
                if (mins > VisitMax) VisitMax = mins;
            }
            catch { }
        }

        internal static void NotePaperSim(BuildingRegistration reg, int candidates, int ordersAdded, int hour)
        {
            try
            {
                try { _passHours.Add(SaveGameManager.Current.Day * 24 + hour); } catch { }
                PaperHours++;
                PaperCandidates += candidates;
                if (ordersAdded > 0) PaperOrders += ordersAdded;
                if (PaperShop.Length == 0 && reg != null) PaperShop = reg.BusinessName ?? "";
            }
            catch { }
        }
    }

    /// <summary>B5: a shopper finishing their own checkout. Customer.CompleteOrder (:385-397) is the
    /// self-checkout route (and the only route a gym customer takes); it is the same method the
    /// till-duplication note names as one of the three live till adds.</summary>
    [HarmonyPatch(typeof(Customer), "CompleteOrder")]
    public static class Patch_Customer_CompleteOrder_ShopDay
    {
        static void Postfix()
        {
            try { ShopDayMeter.LiveCompleteOrders++; ShopDayMeter.SampleBodies(); } catch { }
        }
    }

    /// <summary>B5: a body going home. If its order is not completed it paid nobody - the loss the
    /// set-aside rig run measured (closing time sends a held-back shopper home unpaid).</summary>
    [HarmonyPatch(typeof(Customer), "Leave")]
    public static class Patch_Customer_Leave_ShopDay
    {
        static void Prefix(Customer __instance)
        {
            try
            {
                if (__instance == null) return;
                ShopDayMeter.NoteBodyLeaving(__instance);   // D: spawn -> leave, in game minutes
                var o = __instance.order;
                if (o == null || !o.completed) ShopDayMeter.UnpaidExits++;
            }
            catch { }
        }
    }
}
