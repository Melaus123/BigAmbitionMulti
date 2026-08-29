using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Needs & morale tempo tuning (user design 2026-07-20).
    ///
    /// MP's clock never pauses, so vanilla drain/duration rates feel far faster
    /// in real time than single-player (no pause-thinking, no personal sleep-skip
    /// fast-forwarding debuffs away).  Four host-set single-percent controls
    /// compensate, each "% of native", each with 0/low = gentler:
    ///   • DrainPercent  (default 10)  — energy spend, hunger follows natively
    ///     at 1.5×.  0 drives native disableEnergy (exact native "off": bar
    ///     hidden, NoEat sad-period excluded) — no separate toggle needed.
    ///   • RestPercent   (default 300) — energy regen while resting (bed/bench/
    ///     car/hospital); composes with the native bed-quality bonus.
    ///   • MoraleTempoPercent (default 10, min 1) — ONE dial for morale
    ///     pressure as "% of native speed" (user simplification 2026-07-20):
    ///     POSITIVE modifier durations scale INVERSELY (10% → buffs last 10×;
    ///     the starter honeymoon and every action buff inherit it — negatives
    ///     and permanents stay native; native AddModifier REFRESHES duplicates,
    ///     verified, so stretched buffs re-earned just reset their timer), and
    ///     the sad-period roll scales DIRECTLY (10% → 0.1%/hour at zero
    ///     morale), implemented as a probabilistic VETO on TriggerSadPeriod
    ///     (no native-code rewrite, drift-proof).
    ///
    /// Distribution: new-game/mid-join clients get the values in the settings
    /// DTO (BuildGameVariables applies); ALL clients converge via additive
    /// fields on the 3s GameTimeSync heartbeat (the RainState pattern), which
    /// covers loaded-session joins.  Single-player is untouched (every patch
    /// gates on an MP session).</summary>
    public static class MPNeedsTuning
    {
        public static int DrainPercent  = 10;
        public static int RestPercent   = 300;
        public static int MoralePercent = 10;   // min 1 (0 would mean infinite buffs)

        /// <summary>Derived: multiply positive-buff durations by this (10% → 10×).</summary>
        public static double BuffDurationFactor => 100.0 / Math.Max(1, MoralePercent);

        private static bool InMp => MPServer.IsRunning || MPClient.InMpGame;

        public static void Apply(GameVariablesDto dto, string source)
        {
            if (dto == null) return;
            Set(dto.NeedsDrainPercent, dto.RestSpeedPercent, dto.MoraleTempoPercent, source);
        }

        /// <summary>Heartbeat-side apply (values -1 = absent on older hosts).</summary>
        public static void SetFromHeartbeat(int drain, int rest, int morale)
        {
            if (drain < 0) return;   // older host — keep whatever we have
            Set(drain, rest, morale, "heartbeat");
        }

        private static void Set(int drain, int rest, int morale, string source)
        {
            drain = Math.Max(0, drain); rest = Math.Max(0, rest); morale = Math.Max(1, morale);
            // Round-236: BEFORE the no-change early-return — the flag re-align must run on
            // every heartbeat, not only on value changes (a heartbeat landing during load,
            // before the world exists, would otherwise converge the values once and the
            // flag would never get its turn).
            AlignBakedFlag(drain);
            if (drain == DrainPercent && rest == RestPercent && morale == MoralePercent) return;
            DrainPercent = drain; RestPercent = rest; MoralePercent = morale;
            Plugin.Logger.LogInfo($"[Needs] tuning ({source}): drain={drain}% rest={rest}% moraleTempo={morale}% (buffs ×{BuffDurationFactor:0.#}, before each modifier's own maxHoursDuration cap — see the [Morale] table).");
        }

        /// <summary>Round-236 (field 20260802-231834 "the host is the only person who's hungry",
        /// a many-reports class): the save-baked energy on/off flag silently deadens ALL needs
        /// (hunger drains only inside energy spending) and old MP saves baked it TRUE as the
        /// era's default. The round-53 reconcile un-bakes it — but only on the HOST
        /// (ReconcileLoadedNeedsFlag returns unless MPServer.IsRunning), so every client
        /// character from that era stayed needs-dead forever: hosts hungry, clients never.
        /// Unreproducible on fresh rigs — the flag is state in old saves, not a code path.
        ///
        /// The dial owns the flag on EVERY machine now: each apply (host: host/load/settings;
        /// client: the 3s heartbeat) re-aligns gv.disableEnergy with drain==0. Recurrence-
        /// covered by the heartbeat, so a deliberate drain=0 group keeps needs OFF everywhere
        /// too (a one-shot client fix would have created the inverse asymmetry for them).
        /// The correction re-bakes into the client's save at their next upload — permanent
        /// self-heal, no player action. SP untouched (InMp gate).</summary>
        private static void AlignBakedFlag(int drainPercent)
        {
            try
            {
                if (!InMp) return;
                var gv = SaveGameManager.Current?.gameVariables;
                if (gv == null) return;
                bool want = drainPercent == 0;
                if (gv.disableEnergy == want) return;
                gv.disableEnergy = want;
                Plugin.Logger.LogInfo($"[Needs] baked disableEnergy → {want} (drain dial {drainPercent}% is the authority on every machine — round-236; heals into the save on next upload).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Needs] align flag: {ex.Message}"); }
        }

        // ── Drain: the single native sink (enum overload delegates here).  Hunger
        // follows inside it at 1.5×; the starving ×2 and low-morale amplifier
        // compose on the scaled amount, keeping native semantics.  ────────────
        [HarmonyPatch(typeof(Helpers.EnergyHelper), nameof(Helpers.EnergyHelper.SpentEnergyOnce), typeof(float))]
        public static class Patch_SpentEnergy_DrainScale
        {
            static void Prefix(ref float amount)
            {
                if (InMp && DrainPercent != 100) amount *= DrainPercent / 100f;
            }
        }

        // ── Rest: regen choke point (bed/bench/car/hospital) — composes with the
        // native bed-quality multiplier, which we deliberately do not touch. ──
        [HarmonyPatch(typeof(Helpers.EnergyHelper), nameof(Helpers.EnergyHelper.GenerateEnergy))]
        public static class Patch_GenerateEnergy_RestScale
        {
            static void Prefix(ref float amount)
            {
                if (InMp && RestPercent != 100) amount *= RestPercent / 100f;
            }
        }

        // ── Positive-buff duration: rewrite the duration ARGUMENT so every native
        // path (new entry, refresh, additive) uses the scaled hours consistently.
        // Asset amounts/durations are read from the game's loaded modifier table
        // via reflection (drift-safe).  amount<=0 or duration<=0 (permanent) →
        // untouched.  ─────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(Helpers.HappinessHelper), nameof(Helpers.HappinessHelper.AddModifier))]
        public static class Patch_AddModifier_PositiveDurationScale
        {
            static void Prefix(string type, ref int customHoursDuration)
            {
                try
                {
                    if (!InMp || MoralePercent == 100) return;
                    var dict = AccessTools.Field(typeof(Helpers.HappinessHelper), "Modifiers")?.GetValue(null) as System.Collections.IDictionary;
                    if (dict == null || string.IsNullOrEmpty(type) || !dict.Contains(type)) return;
                    var asset = dict[type];
                    if (asset == null) return;
                    int amount = Convert.ToInt32(AccessTools.Field(asset.GetType(), "amount")?.GetValue(asset) ?? 0);
                    if (amount <= 0) return;   // negatives and neutrals stay native
                    int baseDur = customHoursDuration;
                    if (baseDur == -1) baseDur = Convert.ToInt32(AccessTools.Field(asset.GetType(), "hoursDuration")?.GetValue(asset) ?? -1);
                    if (baseDur <= 0) return;  // permanent — must never become a finite countdown
                    customHoursDuration = Math.Max(1, (int)Math.Round(baseDur * BuffDurationFactor));
                }
                catch { }   // any surprise → native behavior
            }
        }

        // ── Sad-period odds: REMOVED for game 1.0 (2026-08-29). ───────────────
        // 1.0 deleted the entire sad-period subsystem — TriggerSadPeriod, UpdateSadPeriods,
        // CurrentSadPeriod, and the SadPeriod/SadPeriodType types are all gone from the game.
        // The patch class that vetoed the trigger is therefore deleted, not repaired: there is
        // nothing left to veto, and a string-literal target the compiler cannot check made it
        // the one class that failed at startup on 1.0 ("Undefined target method").
        //
        // MoralePercent still does real work — it drives BuffDurationFactor in
        // Patch_AddModifier_PositiveDurationScale above — but its scope is now HALF what the
        // name implies: positive happiness-buff duration only, no sad-period tempo. The host
        // setting's label/tooltip should be revisited if that distinction matters to players.

        // ── One-time morale table dump: the amounts/durations live in game data
        // assets (invisible to the decompile) — print them once per game run so
        // field logs hand us the real economy of morale for tuning. ───────────
        [HarmonyPatch(typeof(Helpers.HappinessHelper), nameof(Helpers.HappinessHelper.OnHappinessModifiersLoaded))]
        public static class Patch_DumpMoraleTable
        {
            private static bool _dumped;
            static void Postfix()
            {
                if (_dumped) return;
                _dumped = true;
                try
                {
                    var dict = AccessTools.Field(typeof(Helpers.HappinessHelper), "Modifiers")?.GetValue(null) as System.Collections.IDictionary;
                    if (dict == null) return;
                    // maxHoursDuration is NEW IN GAME 1.0 (HappinessModifier.cs:15, absent from 0.11)
                    // and it CAPS this dial. Every write of hoursLeft now goes through
                    // HappinessHelper.GetCappedHoursDuration -> Mathf.Min(hoursDuration,
                    // modifier.maxHoursDuration), uncapped only when maxHoursDuration <= 0. So at a
                    // 10% morale setting - a 10x stretch - any modifier with a positive cap is being
                    // silently clamped, and WITHOUT this column a field log cannot show which ones.
                    // The values live in ScriptableObject assets, so reading is not an option: the
                    // only way to learn them is to print them from a running game. That is the whole
                    // reason this dump exists, and it was missing the one field that now decides the
                    // outcome. Printed as "cap:Nh" and omitted entirely when uncapped.
                    var sb = new System.Text.StringBuilder("[Morale] modifier table (name=amount/hours[cap:Nh][/once]): ");
                    foreach (System.Collections.DictionaryEntry e in dict)
                    {
                        var a = e.Value; if (a == null) continue;
                        int amount = Convert.ToInt32(AccessTools.Field(a.GetType(), "amount")?.GetValue(a) ?? 0);
                        int hrs    = Convert.ToInt32(AccessTools.Field(a.GetType(), "hoursDuration")?.GetValue(a) ?? -1);
                        bool once  = Convert.ToBoolean(AccessTools.Field(a.GetType(), "oneTimeOnly")?.GetValue(a) ?? false);
                        int cap    = Convert.ToInt32(AccessTools.Field(a.GetType(), "maxHoursDuration")?.GetValue(a) ?? -1);
                        sb.Append($"{e.Key}={amount:+0;-0}/{hrs}h{(cap > 0 ? $"cap:{cap}h" : "")}{(once ? "/once" : "")}  ");
                    }
                    Plugin.Logger.LogInfo(sb.ToString());
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Morale] table dump: {ex.Message}"); }
            }
        }
    }
}
