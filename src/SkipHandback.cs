using System.Collections.Generic;
using AI.Customers.CustomerEntries;
using HarmonyLib;
using Helpers;   // BusinessTypeHelper - the shop's own BusinessTypeData, and with it the simulator instance

namespace BigAmbitionsMP
{
    /// <summary>H-SKIPWALKIN-1 (batch 26 fold 3, user ruling 2026-09-21) - NO WALKED-IN SHOPPER MAY END
    /// UP BILLED BY NOBODY.
    ///
    /// THE HOLE. During a consensus skip the mod's paper sim bills the occupied shop hour by hour
    /// (Patch_RunHourly_SimulateOccupiedShopDuringSkip) but SETS ASIDE every entry of that hour that a
    /// live body holds, or whose order is already completed, so the sim can never bill the same order
    /// the live checkout bills (H-SKIPDOUBLE-1). A set-aside entry whose shopper then walks out WITHOUT
    /// paying is billed by nobody at all: Customer.Leave (Customer.cs :290-314) puts the items back on
    /// the shelf and sets order.completed, and adds NOTHING to unprocessedCompletedOrders; the
    /// SelfServiceEmployee no-paper-bag branch (SelfServiceEmployee.cs :103-108) does the same itself
    /// before the body ever reaches Leave. At closing time InstantlyLeave sends the whole floor home
    /// that way (Customer.OnNewHour :118-128).
    ///
    /// THE HAND-BACK. On that exit - and only on that exit - the entry is given BACK to the paper
    /// simulator, which is the accountant during a skip (it is vanilla's own fast-forward method).
    /// The order's entries are RESET first (Order.ResetEntries, Order.cs :92-98): the items were just
    /// returned to the shelf, so their stale `processed`/`paid` flags would otherwise make
    /// RetailBusinessSimulator.ProcessCustomer skip every item and still put the order on the till at
    /// yesterday's prices (ProcessCustomer :229-274 only processes entries that are not `processed`,
    /// then completes and tills the order regardless).
    ///
    /// THE HOUR IS THE ENTRY'S OWN SPAWN HOUR, never the current one: at closing time the current hour
    /// has no staffed till, so the simulator would find no point of sale and sell nothing. The
    /// simulator's SetUp(reg, hour) reads the staffing of exactly that hour
    /// (LoadPointsOfSaleWithCustomerSatisfactionThisHour -> GetEmployeeAtStationAndHour).
    ///
    /// NORMAL PLAY IS UNTOUCHED. A shopper who walks out when no skip is running and whose entry was
    /// never set aside is a native lost sale and stays one.
    ///
    /// ACCEPTED DEVIATIONS, both disclosed:
    ///  - the hand-back calls ProcessCustomer directly, so it is not counted against the hour's native
    ///    customer capacity (the cap is private to the simulator and computed inside
    ///    ProcessAllCustomersFromThisHour). This is the same deviation the set-aside already carries.
    ///  - the brief for this fold asked for the "entry not in the set-aside registry but a skip is
    ///    running" case to be left to the COMING hour roll's paper pass. THE CODE SAYS OTHERWISE:
    ///    Customer.Leave does not remove the body from IndoorCustomerSpawner.Customers - it starts the
    ///    LeaveBuilding coroutine, and only ReleaseCustomer (Customer.cs :399-418, :550-553) removes it
    ///    once the body has walked to an exit. At 50x an hour rolls every ~1.2 real seconds, so that
    ///    body is very often still ON THE FLOOR at the roll, the set-aside holds its entry back again,
    ///    and it is billed by nobody - the exact hole this fold exists to close. So that case is handed
    ///    back IMMEDIATELY too, at its own spawn hour. It cannot double-bill: the hand-back completes
    ///    the order, and the coming pass sets every completed-order entry aside.</summary>
    internal static class SkipHandback
    {
        /// <summary>Entry -> the hour whose paper pass ran WITHOUT it. By reference (two entries never
        /// compare equal by value), day-scoped: the game rebuilds the shopper table each day, so the
        /// first touch on a new day drops the old one rather than holding dead entries alive.</summary>
        private static readonly Dictionary<CustomerEntry, int> _aside =
            new Dictionary<CustomerEntry, int>(new TillDupes.RefEq<CustomerEntry>());
        private static int _asideDay = -1;

        internal static int HandBacks, HandBackPaid, HandBackNothing, HandBackLate, HandBackEarly;
        internal static double HandBackRevenue;

        private const int LogBudget = 20;
        private static int _logged;

        private static readonly Dictionary<System.Type, System.Reflection.MethodInfo?> _process =
            new Dictionary<System.Type, System.Reflection.MethodInfo?>();
        private static readonly Dictionary<System.Type, System.Reflection.FieldInfo?> _lastProducer =
            new Dictionary<System.Type, System.Reflection.FieldInfo?>();

        /// <summary>Session boundary (MPRestSync.Reset): no entry, and no counter, may cross it.</summary>
        internal static void Reset()
        {
            try { _aside.Clear(); } catch { }
            _asideDay = -1;
            _logged = 0;
            ClearCounters();
        }

        /// <summary>Window boundary (`shopday reset`): the counters belong to the measured stretch, the
        /// registry does not - a set-aside from before the window is still owed a hand-back.</summary>
        internal static void ClearCounters()
        {
            HandBacks = HandBackPaid = HandBackNothing = HandBackLate = HandBackEarly = 0;
            HandBackRevenue = 0;
        }

        /// <summary>Called once per entry the hourly paper pass held back. The pass only ever looks at
        /// entries of the hour it is closing, so an entry can be recorded at most once and the recorded
        /// hour is always that entry's own spawn hour.</summary>
        internal static void NoteSetAside(CustomerEntry e, int hour)
        {
            try
            {
                if (e == null) return;
                int day = -1;
                try { day = SaveGameManager.Current.Day; } catch { }
                if (day != _asideDay) { _aside.Clear(); _asideDay = day; }
                _aside[e] = hour;
            }
            catch { }
        }

        internal static bool WasSetAside(CustomerEntry e)
        {
            try { return e != null && _aside.ContainsKey(e); } catch { return false; }
        }

        /// <summary>Is ANY reference to this order already on the shop's till? Reference identity only -
        /// the same question TillDupes asks, and the only honest test of "this exit was unpaid".</summary>
        private static bool OrderInTill(BuildingRegistration reg, Order o)
        {
            try
            {
                if (reg == null || o == null) return false;
                var till = reg.unprocessedCompletedOrders;
                if (till == null) return false;
                for (int i = 0; i < till.Count; i++)
                    if (ReferenceEquals(till[i], o)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>The funnel's entry test, taken BEFORE Leave runs so the till answer is the state the
        /// shopper actually left in. Also catches the SelfServiceEmployee no-paper-bag branch, which
        /// sets order.completed itself before the body reaches Leave.</summary>
        internal static bool IsUnpaidExitCandidate(Customer c)
        {
            try
            {
                if (c == null || c.isPlayer) return false;
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null || !MergerFlip.BooksHere(reg)) return false;   // only the machine that holds this shop's books
                var ce = c.customerEntry;
                if (ce == null || ce.order == null) return false;
                if (OrderInTill(reg, ce.order)) return false;                  // they paid - nothing is owed
                if (!WasSetAside(ce) && !MPRestSync.SkipActive) return false;  // a native lost sale in normal play stays one
                return true;
            }
            catch { return false; }
        }

        /// <summary>Hand one walked-out shopper's entry back to the shop's own paper simulator.</summary>
        internal static void HandBack(Customer c)
        {
            try
            {
                if (c == null) return;
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null || !MergerFlip.BooksHere(reg)) return;
                var ce = c.customerEntry;
                if (ce == null || ce.order == null) return;
                var order = ce.order;
                if (OrderInTill(reg, order)) return;                 // re-read at the moment of commitment
                bool late = WasSetAside(ce);
                if (!late && !MPRestSync.SkipActive) return;

                int hour;
                try { hour = ce.spawnTime.Hour; } catch { return; }
                int nowHour = -1;
                try { nowHour = SaveGameManager.Current.Hour; } catch { }
                if (hour < 0 || (nowHour >= 0 && hour > nowHour)) return;   // an entry that has not come due cannot be owed

                var data = BusinessTypeHelper.GetData(reg);
                var sim = data?.simulator;
                if (sim == null || data == null || !data.spawnCustomers) return;

                var t = sim.GetType();
                System.Reflection.MethodInfo? mi;
                if (!_process.TryGetValue(t, out mi))
                {
                    mi = AccessTools.Method(t, "ProcessCustomer", new System.Type[] { typeof(CustomerEntry) });
                    _process[t] = mi;
                }
                if (mi == null) return;                              // no simulator of this shape - leave the order exactly as native left it

                order.ResetEntries();                                // the items are back on the shelf: their paid/processed flags are stale
                order.completed = false;

                System.Reflection.FieldInfo? fi;
                if (!_lastProducer.TryGetValue(t, out fi))
                {
                    fi = AccessTools.Field(t, "lastProducerUsed");
                    _lastProducer[t] = fi;
                }
                try { fi?.SetValue(sim, null); } catch { }           // what the native per-customer loop does before each ProcessCustomer

                sim.SetUp(reg, hour);                                // the staffing of the hour they shopped in, not of closing time
                mi.Invoke(sim, new object[] { ce });                 // virtual dispatch: the gym / cinema / hairdresser override runs

                if (late) { _aside.Remove(ce); HandBackLate++; } else HandBackEarly++;
                HandBacks++;
                double val = 0;
                try { val = TillDupes.ExtraReferenceValue(order); } catch { }
                bool sold = OrderInTill(reg, order) && val > 0;
                if (sold) { HandBackPaid++; HandBackRevenue += val; } else HandBackNothing++;

                if (_logged < LogBudget)
                {
                    _logged++;
                    string shop = "";
                    try { shop = reg.BusinessName ?? ""; } catch { }
                    string outcome = sold ? $"paid ${val:F2}" : "nothing sold";
                    Plugin.Logger.LogInfo($"[Rest] walked-in shopper left unpaid - handed back to the paper sim: '{shop}' "
                                        + $"entry h{hour} -> order {outcome} ({(late ? "set-aside" : "pre-pass")}, H-SKIPWALKIN-1)");
                }
            }
            catch (System.Exception ex)
            {
                try { Plugin.Logger.LogWarning($"[Rest] walked-in hand-back: {ex.Message}"); } catch { }
            }
        }
    }

    /// <summary>H-SKIPWALKIN-1's ONE FUNNEL. Every unpaid exit reaches Customer.Leave: closing time
    /// (OnNewHour -> InstantlyLeave), the behaviour-tree CustomerLeave / WanderAndLeave tasks, and the
    /// self-service no-paper-bag branch (which sets order.completed itself and then tells the customer
    /// to leave). Leave is virtual; overrides call base.Leave(), so patching the base covers them all.
    /// The PREFIX decides - after Leave has run, the items are already back on the shelf and
    /// order.completed reads true whatever happened, so the exit's paid/unpaid truth has to be taken
    /// before it. The POSTFIX acts, so the hand-back re-sells against a shelf that has the items back
    /// on it.</summary>
    [HarmonyPatch(typeof(Customer), "Leave")]
    public static class Patch_Customer_Leave_SkipHandback
    {
        static void Prefix(Customer __instance, out bool __state)
        {
            __state = false;
            try { __state = SkipHandback.IsUnpaidExitCandidate(__instance); } catch { }
        }

        static void Postfix(Customer __instance, bool __state)
        {
            if (!__state) return;
            try { SkipHandback.HandBack(__instance); } catch { }
        }
    }
}
