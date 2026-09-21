using System.Collections.Generic;
using HarmonyLib;
using Buildings;
using Helpers;
using UnityEngine;
using UnityEngine.AI;
using BigAmbitions.Characters;   // AnimationType - CharacterAnimations' parameter type

namespace BigAmbitionsMP
{
    /// <summary>
    /// D-SKIPPACE-1 (user-approved 2026-09-21) - during the mod's consensus time skip the SHOP looks
    /// busy. Every customer and every working staff body in the interior moves and animates at
    /// MPRestSync.SkipPace, capped by MPRestSync.MaxSkipVisualPace (ONE number, dev-settable with the
    /// `skippace` lever; 1000 = the pace IS the skip multiple, user ruling 2026-09-21). The pace belongs to the SHOP, so every player inside sees the same sped-up
    /// floor (user ruling, Option A). A player's OWN body is sped up ONLY while that player is working
    /// a station (MPRestSync.LocalBodyPaced); a free-walking, waiting or seated player is never touched.
    ///
    /// EDGES ONLY - NOTHING PER FRAME. Speeds are written when a skip starts, when the native spawner
    /// creates a body, and when a body starts/stops being a working employee (Employee.SetAgentProperties
    /// / RevertAgentProperties - once per shift edge, not hot). Every base speed we overwrite is
    /// remembered here and written back on the skip's end edge, on MPRestSync.Reset() and on
    /// CustomerPuppets.Reset() (leaving the building): the dictionary is the only place a scaled body
    /// can hide, so "restore everything" is one pass over it.
    /// </summary>
    internal static class SkipPaceBodies
    {
        /// <summary>The three movement limits the GAME had on an agent before we touched it. All three
        /// are scaled together and restored together: speed alone makes a body that slides past its
        /// waypoints and spins slowly on the spot, because acceleration (how fast it reaches that speed
        /// and brakes out of it) and angularSpeed (how fast it turns) are absolutes in the agent.</summary>
        private struct AgentBase { public float Speed; public float Accel; public float Angular; }

        /// <summary>NavMeshAgent -> the limits the GAME had on it before we touched it.</summary>
        private static readonly Dictionary<NavMeshAgent, AgentBase> _bases = new Dictionary<NavMeshAgent, AgentBase>();

        /// <summary>BATCH-26 FOLD 3 (C). Animator -> the Animator.speed the GAME had on it. The
        /// LOOPING activity states (treadmill, sit-ups, trampoline, bench press) are driven by
        /// PermanentAnimationType SetBool (CharacterAnimations.cs :93-97) and carry NO speed
        /// parameter at all - only the one-shot route writes the 'AnimationSpeed' float
        /// (RunAnimationLength :52-58), which is why the user saw a 50x gym where nobody worked out
        /// any faster. Animator.speed is the one knob that reaches every state, so it is what we
        /// scale; it is stored and written back by exactly the same edges as the agent limits.</summary>
        private static readonly Dictionary<Animator, float> _animBases = new Dictionary<Animator, float>();

        /// <summary>Employee components currently on shift. The game keeps NO static registry of live
        /// Employee components (EstimatedWeeklyIncomeHelper builds its own list per call), so the two
        /// native shift edges ARE the registry - written twice per shift, never per frame.</summary>
        private static readonly HashSet<Employee> _onShift = new HashSet<Employee>();

        internal static int ScaledBodies { get { return _bases.Count; } }

        /// <summary>How many animators currently carry our scaled Animator.speed.</summary>
        internal static int ScaledAnimators { get { return _animBases.Count; } }

        /// <summary>Store this animator's speed ONCE, then set it to the pace. Idempotent: a second
        /// call re-uses the stored base, so the pace can never compound. Nothing native writes
        /// Animator.speed (ThirdPersonCharacter winds locomotion through the Forward / MotionTime
        /// parameters instead, :415-445), so this is ours alone until we write it back.</summary>
        internal static bool ApplyAnimator(Animator an)
        {
            try
            {
                if (an == null) return false;
                float pace = MPRestSync.SkipPace;
                if (pace <= 1.001f) return false;
                float bse;
                if (!_animBases.TryGetValue(an, out bse))
                {
                    bse = an.speed;
                    if (bse <= 0.01f) return false;   // a stopped animator has no base to scale
                    _animBases[an] = bse;
                }
                an.speed = bse * pace;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Is THIS animator one we sped up? The RunAnimationLength postfix divides a returned
        /// clip length only for these, so the picture and the wait behind it can never disagree.</summary>
        internal static bool IsAnimatorScaled(Animator an)
        {
            try { return an != null && _animBases.ContainsKey(an); }
            catch { return false; }
        }

        /// <summary>Write one animator's speed back and drop it (a body that stops being paced).</summary>
        internal static void RestoreAnimatorOne(Animator an)
        {
            try
            {
                if (an == null) return;
                float bse;
                if (_animBases.TryGetValue(an, out bse)) { try { an.speed = bse; } catch { } }
                _animBases.Remove(an);
            }
            catch { }
        }

        /// <summary>How many RunAnimationLength calls we scaled this session (dev lever reads it).</summary>
        internal static int AnimScaled;
        private static int _animLogged;

        /// <summary>Metres per second no paced body may pass. 1000 = OFF in practice (user ruling
        /// 2026-09-21: the pace is the real skip multiple and the user judges the look), where it used to
        /// be the game's own RUN speed (GlobalReferences.walkingSpeedRun, 6). Lower it with the
        /// `skippace walkmax &lt;m/s&gt;` DEV lever arm to put the old ceiling back on one machine.</summary>
        public static float MaxPacedBodySpeed = 1000f;

        /// <summary>Store this agent's three movement limits ONCE, then scale all three by the pace:
        /// speed = min(base * pace, max(base, MaxPacedBodySpeed)), acceleration and angularSpeed straight
        /// * pace so a fast body can still turn corners and stop at its waypoint instead of sliding
        /// through it. Idempotent: a second Apply on the same agent re-uses the stored bases, so the pace
        /// can never compound.</summary>
        internal static bool Apply(NavMeshAgent a)
        {
            try
            {
                if (a == null) return false;
                float pace = MPRestSync.SkipPace;
                if (pace <= 1.001f) return false;
                AgentBase bse;
                if (!_bases.TryGetValue(a, out bse))
                {
                    bse.Speed = a.speed; bse.Accel = a.acceleration; bse.Angular = a.angularSpeed;
                    if (bse.Speed <= 0.01f) return false;   // a stopped/disabled agent has no base to scale
                    _bases[a] = bse;
                }
                a.speed = Mathf.Min(bse.Speed * pace, Mathf.Max(bse.Speed, MaxPacedBodySpeed));
                // B4 BRAKING (batch 26 fold 2). Acceleration is scaled by the pace SQUARED, not by the
                // pace: a NavMeshAgent's braking distance is v^2 / 2a, so with v multiplied by N the
                // distance only stays the same if a is multiplied by N^2. At `* pace` a 50x body needed
                // 50x the room to stop and slid through its waypoint. angularSpeed stays `* pace` - a
                // turn is an angle covered in time, and time is what the pace divides. Both are stored
                // and written back by the same RestoreAll/Write pair as before, so the restore is
                // unchanged.
                if (bse.Accel   > 0.01f) a.acceleration = bse.Accel * pace * pace;
                if (bse.Angular > 0.01f) a.angularSpeed = bse.Angular * pace;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Write one agent's base limits back and drop it (used by the shift-start PREFIX, so
        /// the game latches the TRUE speed into its own _oldSpeed).</summary>
        internal static void RestoreOne(NavMeshAgent a)
        {
            try
            {
                if (a == null) return;
                AgentBase bse;
                if (_bases.TryGetValue(a, out bse)) { try { Write(a, bse); } catch { } }
                _bases.Remove(a);
            }
            catch { }
        }

        /// <summary>The one place a stored base is written back onto an agent.</summary>
        private static void Write(NavMeshAgent a, AgentBase bse)
        {
            a.speed = bse.Speed;
            if (bse.Accel   > 0.01f) a.acceleration = bse.Accel;
            if (bse.Angular > 0.01f) a.angularSpeed = bse.Angular;
        }

        /// <summary>Drop an agent WITHOUT writing anything - the native code just set the speed itself,
        /// so its value is the truth from here on.</summary>
        internal static void Forget(NavMeshAgent a)
        {
            try { if (a != null) _bases.Remove(a); } catch { }
        }

        /// <summary>The SkipActive FALSE-&gt;TRUE edge: every body already standing in this interior.</summary>
        internal static void OnSkipStarted()
        {
            try
            {
                float pace = MPRestSync.SkipPace;
                if (pace <= 1.001f) return;   // cap of 1 = feature off on this machine
                int c = 0, e = 0;
                bool player = false;
                try
                {
                    var list = IndoorCustomerSpawner.Customers;   // the NATIVE registry, never a scene scan
                    for (int i = 0; i < list.Count; i++)
                    {
                        var cu = list[i];
                        if (cu == null || cu.tpc == null) continue;
                        ApplyAnimator(cu.tpc.animator);   // C: the looping activity states follow the pace too
                        if (Apply(cu.tpc.navmeshAgent)) c++;
                    }
                }
                catch { }
                try
                {
                    foreach (var emp in _onShift)
                    {
                        if (emp == null || emp.employeeTpc == null) continue;
                        if (emp.isPlayer)
                        {
                            if (MPRestSync.LocalBodyPaced)
                            {
                                ApplyAnimator(emp.employeeTpc.animator);
                                if (Apply(emp.employeeTpc.navmeshAgent)) player = true;
                            }
                        }
                        else
                        {
                            ApplyAnimator(emp.employeeTpc.animator);
                            if (Apply(emp.employeeTpc.navmeshAgent)) e++;
                        }
                    }
                }
                catch { }
                try { Plugin.Logger.LogInfo($"[SkipPace] applied x{pace:0.##} to {c} customer(s), {e} staff, player={player}, animators={_animBases.Count}"); } catch { }
            }
            catch { }
        }

        /// <summary>A customer the native spawner created DURING a skip.</summary>
        internal static void OnCustomerSpawned(Customer c)
        {
            try { if (c != null && c.tpc != null) { ApplyAnimator(c.tpc.animator); Apply(c.tpc.navmeshAgent); } } catch { }
        }

        internal static void NoteShiftStart(Employee emp)
        {
            try
            {
                if (emp == null || emp.employeeTpc == null) return;
                _onShift.Add(emp);
                var a = emp.employeeTpc.navmeshAgent;
                if (a == null) return;
                if (!emp.isPlayer) { ApplyAnimator(emp.employeeTpc.animator); Apply(a); }
                else if (MPRestSync.LocalBodyPaced) { ApplyAnimator(emp.employeeTpc.animator); Apply(a); }   // MY body: only while I am working a station
            }
            catch { }
        }

        internal static void NoteShiftEnd(Employee emp)
        {
            try
            {
                if (emp == null) return;
                _onShift.Remove(emp);
                // RevertAgentProperties already wrote the native speed back (for the player through
                // Character.SetWalkingSpeed), so ours must NOT be written over it.
                if (emp.employeeTpc != null)
                {
                    Forget(emp.employeeTpc.navmeshAgent);
                    RestoreAnimatorOne(emp.employeeTpc.animator);   // C: nothing native puts Animator.speed back, so we must
                }
            }
            catch { }
        }

        /// <summary>Put EVERY scaled body back. Called on the skip's end edge, on MPRestSync.Reset()
        /// and on CustomerPuppets.Reset() (leaving the building / session end).</summary>
        internal static void RestoreAll()
        {
            int n = 0;
            try
            {
                foreach (var kv in _bases)
                {
                    var a = kv.Key;
                    if (a == null) continue;   // destroyed body - nothing to write back
                    try { Write(a, kv.Value); n++; } catch { }
                }
            }
            catch { }
            try { _bases.Clear(); } catch { }
            int na = 0;
            try
            {
                foreach (var kv in _animBases)
                {
                    if (kv.Key == null) continue;        // destroyed body - nothing to write back
                    try { kv.Key.speed = kv.Value; na++; } catch { }
                }
            }
            catch { }
            try { _animBases.Clear(); } catch { }
            if (n > 0 || na > 0) { try { Plugin.Logger.LogInfo($"[SkipPace] restored {n} body speed(s), {na} animator speed(s)"); } catch { } }
        }

        /// <summary>Counter + a LOG BUDGET of 10 lines (first scaled call, then every 200th).</summary>
        internal static void NoteAnimScaled(float pace)
        {
            AnimScaled++;
            if (_animLogged >= 10) return;
            if (AnimScaled == 1 || AnimScaled % 200 == 0)
            {
                _animLogged++;
                try { Plugin.Logger.LogInfo($"[SkipPace] animation x{pace:0.##} ({AnimScaled} so far)"); } catch { }
            }
        }
    }

    /// <summary>D-SKIPPACE-1 ANIMATION, REBUILT IN BATCH-26 FOLD 3 (C).
    ///
    /// WHAT WAS WRONG. This used to be a PREFIX that multiplied RunAnimationLength's `speed`
    /// parameter. That parameter only reaches the 'AnimationSpeed' float
    /// (CharacterAnimations.cs :52-58), which is the ONE-SHOT route. The gym's activities - treadmill,
    /// sit-ups, trampoline, bench press - are LOOPING states set through
    /// CharacterAnimations.SetBool(PermanentAnimationType) (:93-97), which writes no speed at all, so
    /// the user correctly saw a 50x gym in which nobody worked out any faster.
    ///
    /// WHAT IT IS NOW. The pace is applied ONCE PER BODY, as Animator.speed, on exactly the edges the
    /// agent speeds use (SkipPaceBodies.ApplyAnimator / RestoreAll). Animator.speed reaches every
    /// state, looping and one-shot alike, so nothing is left behind.
    ///
    /// WHY THIS IS A POSTFIX ON THE LENGTH, NOT A PREFIX ON THE SPEED. With Animator.speed already
    /// carrying the pace, ALSO multiplying the AnimationSpeed float would scale a one-shot twice and
    /// the returned length once - the picture and the WaitForSeconds behind it (RunAnimation :43-50)
    /// would disagree. So the float is left exactly as the game set it and the RETURNED length is
    /// divided instead: one scaling, and the wait matches the clip. The division applies only to
    /// animators SkipPaceBodies actually scaled, which is what makes those two always agree.
    ///
    /// LOCOMOTION IS NOT DOUBLED. ThirdPersonCharacter winds walking through the Forward and
    /// MotionTime parameters by hand (:415-445); a state driven by Motion Time takes its time from
    /// that parameter, not from Animator.speed, so the sped-up agent and the sped-up animator do not
    /// multiply. WHO GETS SCALED is unchanged and lives at the apply edges: customers and working
    /// staff in the interior, the local player only while MPRestSync.LocalBodyPaced, remote players'
    /// avatars never (their animator parameters come off the wire).</summary>
    [HarmonyPatch(typeof(CharacterAnimations), "RunAnimationLength")]
    public static class Patch_CharacterAnimations_SkipPace
    {
        static void Postfix(Animator animator, ref float __result)
        {
            try
            {
                if (animator == null || __result <= 0f) return;
                float pace = MPRestSync.SkipPace;
                if (pace <= 1.001f) return;                          // not skipping, or the cap is 1
                if (!SkipPaceBodies.IsAnimatorScaled(animator)) return;   // this body is not paced: its clip runs at native speed
                __result /= pace;
                SkipPaceBodies.NoteAnimScaled(pace);
            }
            catch { }
        }
    }

    /// <summary>D-SKIPPACE-1 WALKING (staff). There is no native registry of live Employee components,
    /// so these two shift edges are it. NOT hot: SetAgentProperties runs when a body starts working a
    /// station, RevertAgentProperties when it stops.
    /// THE PREFIX MATTERS: SetAgentProperties LATCHES navmeshAgent.speed into its own _oldSpeed, so a
    /// scaled value still on the agent would become that employee's "original" speed forever. We put
    /// ours back first and the game latches the truth.</summary>
    [HarmonyPatch(typeof(Employee), "SetAgentProperties")]
    public static class Patch_Employee_SkipPaceOn
    {
        static void Prefix(Employee __instance)
        {
            try
            {
                if (__instance == null || __instance.employeeTpc == null) return;
                SkipPaceBodies.RestoreOne(__instance.employeeTpc.navmeshAgent);
            }
            catch { }
        }

        static void Postfix(Employee __instance)
        {
            try { SkipPaceBodies.NoteShiftStart(__instance); } catch { }
        }
    }

    [HarmonyPatch(typeof(Employee), "RevertAgentProperties")]
    public static class Patch_Employee_SkipPaceOff
    {
        static void Postfix(Employee __instance)
        {
            try { SkipPaceBodies.NoteShiftEnd(__instance); } catch { }
        }
    }

    /// <summary>D-SKIPPACE-1 WALKING (a customer spawned DURING a skip). The one funnel where a body
    /// joins IndoorCustomerSpawner.Customers is the private static SpawnCustomer(CustomerEntry) - it
    /// calls the CustomerType overload that does Customers.Add, then Init()s the body, so the agent
    /// exists by the time this postfix runs. The mod had no patch here (CustomerPuppets only reflects
    /// the same method in order to CALL it), so this is a new one rather than an extension.</summary>
    [HarmonyPatch(typeof(IndoorCustomerSpawner), "SpawnCustomer",
                  new System.Type[] { typeof(AI.Customers.CustomerEntries.CustomerEntry) })]
    public static class Patch_IndoorSpawner_SkipPaceNewBody
    {
        // Review M1: native SpawnCustomer returns WITHOUT a body when the entrance fee is refused
        // (IndoorCustomerSpawner.cs ~:252-256), so 'the last element' may be somebody else's body.
        static void Prefix(out int __state)
        {
            __state = -1;
            try { __state = IndoorCustomerSpawner.Customers?.Count ?? -1; } catch { }
        }

        static void Postfix(int __state)
        {
            try
            {
                var list = IndoorCustomerSpawner.Customers;
                if (list == null || __state < 0 || list.Count <= __state) return;      // nothing was appended by this call
                var body = list[list.Count - 1];                                         // the body this call just added
                ShopDayMeter.NoteBodySpawned(body);   // B5: EVERY body, skip or not - this is the arrivals census
                if (!MPRestSync.SkipActive || body == null) return;
                SkipPaceBodies.OnCustomerSpawned(body);
            }
            catch { }
        }
    }

    /// <summary>B2 (batch 26 fold 2; KEPT in fold 3 after the LIVE mode was deleted) - THE SPAWNER
    /// POLLS AT PACE. It is now gated on 'a skip is running and the shop is sped up' alone, and what it
    /// buys is SPREAD, not extra income: the day's arrivals are the game's own schedule either way, but
    /// polling once per REAL second at 50x dumps up to fifty game-minutes of them in one burst, while
    /// polling once per sped-up shop-second lets them trickle in the way a normal-speed floor fills.
    /// The arrivals throttle (Patch_IndoorSpawner_SkipVisualPace) still decides who actually gets a
    /// body, and every entry that does not is billed on paper by the hourly sim.
    /// Native IndoorCustomerSpawner.Update polls TrySpawnCustomer once per REAL second
    /// (`_nextSpawnCheck -= Time.deltaTime; ... _nextSpawnCheck = 1f`, IndoorCustomerSpawner.cs:135-160).
    /// At 50 game-minutes per real second that one poll has to cover 50 game-minutes of the day's
    /// arrivals, so the shop starves. Pulling the countdown down to 1/pace makes the spawner poll
    /// once per sped-up shop-second: the game's OWN schedule decides who arrives and how many, we
    /// only ask it at the rate the shop is now living at.
    /// THE ONE PER-FRAME HOOK of this work. The hook itself is a compare-and-assign, but what it
    /// CAUSES is not free (review M2): every poll is a native TrySpawnCustomer = a walk over the
    /// day's entry list for this address. The countdown is therefore floored at 0.03 s (about 30
    /// polls a second at most, instead of one per frame); at 50x that is still one poll per ~1.5
    /// shop-minutes. One spawner exists per interior.</summary>
    [HarmonyPatch(typeof(IndoorCustomerSpawner), "Update")]
    public static class Patch_IndoorSpawner_LivePaceArrivals
    {
        static void Postfix(IndoorCustomerSpawner __instance)
        {
            try
            {
                if (!MPRestSync.SkipActive) return;
                float pace = MPRestSync.SkipPace;
                if (pace <= 1.001f) return;
                float want = Mathf.Max(1f / pace, 0.03f);
                if (__instance._nextSpawnCheck > want) __instance._nextSpawnCheck = want;
            }
            catch { }
        }
    }

    /// <summary>B3 (batch 26 fold 2) - THE REMAINING REAL-SECOND WAITS. Walking and animation already
    /// follow the pace (D-SKIPPACE-1); what was left were waits measured in REAL seconds that no
    /// amount of speed touches, so a 50x shopper still stood still for 3 or 4 whole seconds. Each one
    /// is taken at the NARROWEST hook that exists for it:
    ///   - a duration PARAMETER  -> a Prefix that divides it (the coroutine stub copies the parameter
    ///     into its state machine, so dividing it before the stub runs divides the real wait);
    ///   - a stored `Time.time + x` DEADLINE -> a Postfix that pulls the deadline in.
    /// WaitForSeconds itself is never patched. Gating is exactly Patch_CharacterAnimations_SkipPace's:
    /// a skip is running, the local player is inside a registered interior, and the body is neither a
    /// remote player's avatar nor the local player unless that player is working a station.</summary>
    internal static class SkipPaceWaits
    {
        /// <summary>The pace to divide a wait by, or 1 to leave it alone. Same predicate as the
        /// animation prefix - indoors, in a registered building, during a skip.</summary>
        internal static float InteriorPace()
        {
            try
            {
                float pace = MPRestSync.SkipPace;
                if (pace <= 1.001f) return 1f;
                if (!BuildingManager.IsInsideBuilding) return 1f;
                if (InstanceBehavior<BuildingManager>.Instance?.buildingRegistration == null) return 1f;
                return pace;
            }
            catch { return 1f; }
        }

        /// <summary>Is THIS body one the shop's pace owns? Remote players' avatars never (their
        /// animation and position come off the wire); the local player only while working a station.</summary>
        internal static bool BodyIsPaced(ThirdPersonCharacter tpc)
        {
            try
            {
                if (tpc == null) return false;
                if (RemotePlayerManager.IsRemoteAvatarTransform(tpc.transform)) return false;
                if (tpc.isPlayer) return MPRestSync.LocalBodyPaced;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Pull a real-time deadline in so the time LEFT on it is divided by the pace. Never
        /// pushes a deadline out, never touches one that has already passed.</summary>
        internal static void PullIn(ref float deadline)
        {
            try
            {
                float pace = InteriorPace();
                if (pace <= 1.001f) return;
                float now = Time.time;
                float left = deadline - now;
                if (left <= 0.0001f) return;
                deadline = now + left / pace;
            }
            catch { }
        }
    }

    /// <summary>B3 (a) ThirdPersonCharacter.ShowExpression(~:747-759): `yield return new
    /// WaitForSeconds(secondsToShow)` AND the emoji bubble's own lifetime both come from the
    /// secondsToShow PARAMETER, so dividing it once shortens the picture and the wait together. This
    /// is also the single funnel behind CharacterShowEmojiExpression.StartShowingEmoji, which is how
    /// a shopper's 4-second "can't find it / too expensive" complaint is timed
    /// (TryGrabItemBase.Complain) - so that wait is covered here too, not separately.</summary>
    [HarmonyPatch(typeof(ThirdPersonCharacter), "ShowExpression")]
    public static class Patch_Tpc_ShowExpression_SkipPace
    {
        static void Prefix(ThirdPersonCharacter __instance, ref float secondsToShow)
        {
            try
            {
                float pace = SkipPaceWaits.InteriorPace();
                if (pace <= 1.001f) return;
                if (!SkipPaceWaits.BodyIsPaced(__instance)) return;
                if (secondsToShow > 0.0001f) secondsToShow /= pace;
            }
            catch { }
        }
    }

    /// <summary>B3 (b) Customer.CustomerDemandExpressionCoroutine(~:157-162): `yield return new
    /// WaitForSeconds(delay)` on a PARAMETER, then a 3-second ShowExpression that (a) above already
    /// scales. Dividing the parameter covers the first wait.</summary>
    [HarmonyPatch(typeof(Customer), "CustomerDemandExpressionCoroutine")]
    public static class Patch_Customer_DemandExpression_SkipPace
    {
        static void Prefix(Customer __instance, ref float delay)
        {
            try
            {
                float pace = SkipPaceWaits.InteriorPace();
                if (pace <= 1.001f) return;
                if (__instance == null || !SkipPaceWaits.BodyIsPaced(__instance.tpc)) return;
                if (delay > 0.0001f) delay /= pace;
            }
            catch { }
        }
    }

    /// <summary>B3 (c) ThirdPersonCharacter.MoveToPosition(~:684): the walk itself is paced by the
    /// agent, but the coroutine opens with `yield return new WaitForSeconds(delay)` on a PARAMETER -
    /// a real-second pause before the body even sets off. Only the Vector3 overload is patched: the
    /// Transform overload is not a coroutine, it just calls this one, so both routes land here.</summary>
    [HarmonyPatch(typeof(ThirdPersonCharacter), "MoveToPosition",
                  new System.Type[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(bool),
                                      typeof(UnityEngine.Events.UnityAction), typeof(float),
                                      typeof(UnityEngine.Events.UnityAction), typeof(bool) })]
    public static class Patch_Tpc_MoveToPosition_SkipPace
    {
        static void Prefix(ThirdPersonCharacter __instance, ref float delay)
        {
            try
            {
                float pace = SkipPaceWaits.InteriorPace();
                if (pace <= 1.001f) return;
                if (!SkipPaceWaits.BodyIsPaced(__instance)) return;
                if (delay > 0.0001f) delay /= pace;
            }
            catch { }
        }
    }

    /// <summary>B3 (d) SelfServiceCustomerTryGrabItem.OnAnimationFinished(~:33) - after taking an item
    /// off the shelf the task idles until a STORED deadline, `stopWaitingTime = Time.time +
    /// Random.Range(0.2f, 0.8f)` (TryGrabItemBase.OnUpdate returns Running while
    /// `stopWaitingTime > Time.time`). There is no duration parameter to divide, so the deadline is
    /// pulled in after the native line has written it.</summary>
    [HarmonyPatch(typeof(SelfServiceCustomerTryGrabItem), "OnAnimationFinished")]
    public static class Patch_SelfServiceGrab_SkipPace
    {
        static void Postfix(SelfServiceCustomerTryGrabItem __instance)
        {
            try { if (__instance != null) SkipPaceWaits.PullIn(ref __instance.stopWaitingTime); }
            catch { }
        }
    }

    /// <summary>B3 (e) GymCustomerTryGrabItem.OnAnimationFinished - the same stored deadline on the
    /// gym's own grab task.</summary>
    [HarmonyPatch(typeof(GymCustomerTryGrabItem), "OnAnimationFinished")]
    public static class Patch_GymGrab_SkipPace
    {
        static void Postfix(GymCustomerTryGrabItem __instance)
        {
            try { if (__instance != null) SkipPaceWaits.PullIn(ref __instance.stopWaitingTime); }
            catch { }
        }
    }
}
