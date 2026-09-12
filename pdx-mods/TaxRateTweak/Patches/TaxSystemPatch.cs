using Game.Economy;
using Game.Simulation;
using HarmonyLib;
using Unity.Mathematics;

namespace TaxRateTweak.Patches
{
    /// <summary>
    /// Core patches against the simulation tax system. The approach is vanilla-style: instead of
    /// touching anything per frame, the <c>m_TaxParameterData</c> ranges are expanded once at each
    /// point where the game itself (re)loads that data — OnCreate / OnGameLoaded / PostDeserialize /
    /// SetDefaults / EnsureTaxParameterData — and written back through a compiled field-reference
    /// delegate (<see cref="AccessTools.FieldRefAccess{T, F}(string)"/>), which runs at the same
    /// speed as the game's own field access (no reflection, no boxing, nothing per frame).
    ///
    /// Signatures were confirmed by reflecting against the local Game.dll:
    ///   - OnCreate / OnGameLoaded / PostDeserialize / SetDefaults / EnsureTaxParameterData
    ///   - GetTaxParameterData()                                 // public getter (the limit ranges below)
    ///   - SetTaxRate(TaxAreaType, int)                          // public setter (area type)
    ///   - SetResidential/Commercial/Industrial/OfficeTaxRate    // public setters (sub-rates)
    ///
    /// NOTE: GetTaxRateRange / GetJobLevelTaxRateRange / GetResourceTaxRateRange are NOT patched.
    /// Those methods return the *current spread* of an area's sub-rates (job levels / resources),
    /// not the clamping window. The in-game taxation panel binds `areaResourceTaxRanges` to
    /// GetTaxRateRange; overriding it to (-1000, +100) made a single 8% rate display as an absurd
    /// "-1000%..+100%" range. The clamping window lives in m_TaxParameterData, expanded below.
    /// </summary>
    [HarmonyPatch(typeof(TaxSystem))]
    public static class TaxSystemPatch
    {
        // 惰性生成的直接字段访问委托：首次调用时编译出与游戏自身字段访问等价的动态方法，
        // 之后对 m_TaxParameterData 的读写都是直接引用操作——无反射、无装箱，与原版同速。
        private static AccessTools.FieldRef<TaxSystem, Game.Prefabs.TaxParameterData> s_DataRef;
        private static bool s_DataRefTried;

        private static AccessTools.FieldRef<TaxSystem, Game.Prefabs.TaxParameterData> DataRef
        {
            get
            {
                if (!s_DataRefTried)
                {
                    s_DataRefTried = true;
                    try
                    {
                        s_DataRef = AccessTools.FieldRefAccess<TaxSystem, Game.Prefabs.TaxParameterData>("m_TaxParameterData");
                    }
                    catch (System.Exception)
                    {
                        s_DataRef = null;
                    }
                }
                return s_DataRef;
            }
        }

        // ------------------------------------------------------------------
        // 生命周期挂钩：游戏只在这些时刻（重新）装载税率参数数据，
        // 我们紧跟其后把范围扩到 -1000% .. +100%。稳态下完全零开销。
        // ------------------------------------------------------------------

        [HarmonyPatch("OnCreate")]
        [HarmonyPostfix]
        public static void OnCreatePostfix(TaxSystem __instance)
        {
            Expand(__instance);
        }

        [HarmonyPatch("OnGameLoaded")]
        [HarmonyPostfix]
        public static void OnGameLoadedPostfix(TaxSystem __instance) => Expand(__instance);

        [HarmonyPatch("PostDeserialize")]
        [HarmonyPostfix]
        public static void PostDeserializePostfix(TaxSystem __instance) => Expand(__instance);

        [HarmonyPatch("SetDefaults")]
        [HarmonyPostfix]
        public static void SetDefaultsPostfix(TaxSystem __instance) => Expand(__instance);

        // 游戏自己"确保税率参数就绪"的入口；若它从预制体重新装载了数据，立即重新扩展。
        [HarmonyPatch("EnsureTaxParameterData")]
        [HarmonyPostfix]
        public static void EnsureTaxParameterDataPostfix(TaxSystem __instance) => Expand(__instance);

        /// <summary>
        /// Expand all range fields of <c>m_TaxParameterData</c> (which the simulation clamps
        /// against via EnsureAreaTaxRateLimits / EnsureJobLevelTaxRateLimits / ClampResourceTaxRates)
        /// so every rate (including residential job-level / education sub-rates and
        /// commercial/industrial/office resource sub-rates) can go to -1000% .. +100%.
        /// </summary>
        private static void Expand(TaxSystem instance)
        {
            if (!Settings.Enabled)
            {
                return;
            }

            var dataRef = DataRef;
            if (dataRef == null)
            {
                return;
            }

            try
            {
                var range = Settings.GetRange();

                // 直接引用实例字段，就地修改，无需任何读写拷贝。
                ref Game.Prefabs.TaxParameterData data = ref dataRef(instance);
                data.m_TotalTaxLimits = range;
                data.m_ResidentialTaxLimits = range;
                data.m_CommercialTaxLimits = range;
                data.m_IndustrialTaxLimits = range;
                data.m_OfficeTaxLimits = range;
                data.m_JobLevelTaxLimits = range;
                data.m_ResourceTaxLimits = range;
            }
            catch (System.Exception ex)
            {
                if (Settings.DebugLogging)
                {
                    Mod.log.Warn($"Expand failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Expand every tax-limit range on the cached <see cref="Game.Prefabs.TaxParameterData"/>.
        /// This is the single data source for both the simulation clamps (EnsureAreaTaxRateLimits /
        /// EnsureJobLevelTaxRateLimits / ClampResourceTaxRates) and the in-game taxation panel
        /// (which reads m_TotalTaxLimits and the per-area/resource/job-level limits). By expanding
        /// all of them here, every tax rate (including the residential job-level / education
        /// sub-rates and commercial/industrial/office resource sub-rates) can be dragged to ±1000%.
        /// </summary>
        [HarmonyPatch(nameof(TaxSystem.GetTaxParameterData))]
        [HarmonyPostfix]
        public static void GetTaxParameterDataPostfix(ref Game.Prefabs.TaxParameterData __result)
        {
            if (!Settings.Enabled)
            {
                return;
            }

            var range = Settings.GetRange();
            __result.m_TotalTaxLimits = range;
            __result.m_ResidentialTaxLimits = range;
            __result.m_CommercialTaxLimits = range;
            __result.m_IndustrialTaxLimits = range;
            __result.m_OfficeTaxLimits = range;
            __result.m_JobLevelTaxLimits = range;
            __result.m_ResourceTaxLimits = range;
        }

        // ------------------------------------------------------------------
        // Defense in depth: also clamp rates written through the public setters
        // so nothing can escape the configured window even if a caller bypasses
        // the "range" methods.
        // ------------------------------------------------------------------

        [HarmonyPatch(nameof(TaxSystem.SetTaxRate))]
        [HarmonyPrefix]
        public static void SetTaxRatePrefix(TaxAreaType areaType, ref int rate)
        {
            if (Settings.Enabled)
            {
                rate = Settings.ClampRate(rate);
            }
        }

        [HarmonyPatch(nameof(TaxSystem.SetResidentialTaxRate))]
        [HarmonyPrefix]
        public static void SetResidentialTaxRatePrefix(int jobLevel, ref int rate)
        {
            if (Settings.Enabled)
            {
                rate = Settings.ClampRate(rate);
            }
        }

        [HarmonyPatch(nameof(TaxSystem.SetCommercialTaxRate))]
        [HarmonyPrefix]
        public static void SetCommercialTaxRatePrefix(Resource resource, ref int rate)
        {
            if (Settings.Enabled)
            {
                rate = Settings.ClampRate(rate);
            }
        }

        [HarmonyPatch(nameof(TaxSystem.SetIndustrialTaxRate))]
        [HarmonyPrefix]
        public static void SetIndustrialTaxRatePrefix(Resource resource, ref int rate)
        {
            if (Settings.Enabled)
            {
                rate = Settings.ClampRate(rate);
            }
        }

        [HarmonyPatch(nameof(TaxSystem.SetOfficeTaxRate))]
        [HarmonyPrefix]
        public static void SetOfficeTaxRatePrefix(Resource resource, ref int rate)
        {
            if (Settings.Enabled)
            {
                rate = Settings.ClampRate(rate);
            }
        }
    }
}
