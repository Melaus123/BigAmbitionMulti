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
            if (drain == DrainPercent && rest == RestPercent && morale == MoralePercent) return;
            DrainPercent = drain; RestPercent = rest; MoralePercent = morale;
            Plugin.Logger.LogInfo($"[Needs] tuning ({source}): drain={drain}% rest={rest}% moraleTempo={morale}% (buffs ×{BuffDurationFactor:0.#}, sad roll {morale}% of native).");
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

        // ── Sad-period odds: probabilistic veto on the trigger — native rolls its
        // 1%/hour, we pass SadChancePercent% of the triggers through.  Also
        // covers the console command (harmless).  ─────────────────────────────
        [HarmonyPatch(typeof(Helpers.HappinessHelper), "TriggerSadPeriod")]
        public static class Patch_TriggerSadPeriod_ChanceScale
        {
            static bool Prefix()
            {
                if (!InMp || MoralePercent >= 100) return true;
                if (UnityEngine.Random.Range(0f, 100f) < MoralePercent) return true;
                Plugin.Logger.LogInfo($"[Needs] sad-period trigger vetoed (morale tempo {MoralePercent}% of native).");
                return false;
            }
        }

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
                    var sb = new System.Text.StringBuilder("[Morale] modifier table (name=amount/hours[/once]): ");
                    foreach (System.Collections.DictionaryEntry e in dict)
                    {
                        var a = e.Value; if (a == null) continue;
                        int amount = Convert.ToInt32(AccessTools.Field(a.GetType(), "amount")?.GetValue(a) ?? 0);
                        int hrs    = Convert.ToInt32(AccessTools.Field(a.GetType(), "hoursDuration")?.GetValue(a) ?? -1);
                        bool once  = Convert.ToBoolean(AccessTools.Field(a.GetType(), "oneTimeOnly")?.GetValue(a) ?? false);
                        sb.Append($"{e.Key}={amount:+0;-0}/{hrs}h{(once ? "/once" : "")}  ");
                    }
                    Plugin.Logger.LogInfo(sb.ToString());
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Morale] table dump: {ex.Message}"); }
            }
        }
    }
}
