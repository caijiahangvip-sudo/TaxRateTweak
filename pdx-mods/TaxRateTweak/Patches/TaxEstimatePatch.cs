using Game.City;
using Game.Simulation;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;

namespace TaxRateTweak.Patches
{
    /// <summary>
    /// 预算/税收面板的住宅税"预估"来自 TaxSystem.GetEstimatedTaxAmount，
    /// 只含原生税率 × 应税收入统计，不含分密度附加税。
    /// 附加税的结算节奏恰好是每 262144 帧一次 = 面板的"每月"
    /// （TimeSettingsPrefab.m_DaysPerYear=12：1 模拟日就是 1 个日历月），
    /// 所以上次结算的实收总额可以直接当月度预估加进去，预估与实收口径即对齐。
    /// 补贴（附加税总额为负）同样计入——Any 语义下正负都反映。
    /// </summary>
    [HarmonyPatch(typeof(TaxSystem), "GetEstimatedTaxAmount",
        new[] { typeof(TaxAreaType), typeof(TaxResultType),
                typeof(NativeParallelHashMap<CityStatisticsSystem.StatisticsKey, Entity>),
                typeof(BufferLookup<CityStatistic>) })]
    public static class TaxEstimatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(TaxAreaType areaType, TaxResultType resultType, ref int __result)
        {
            if (!Settings.Enabled || !Settings.EnableDensityTax || areaType != TaxAreaType.Residential)
            {
                return;
            }
            long extra = 0;
            for (int i = 0; i < 5; i++)
            {
                extra += Systems.DensityTaxSystem.GetLastSettled(i);
            }
            if (extra == 0)
            {
                return;
            }
            // 沿用原生 MatchesResultType 语义：Income 只加正数、Expense 只加负数、Any 都加。
            if (resultType == TaxResultType.Income && extra < 0)
            {
                return;
            }
            if (resultType == TaxResultType.Expense && extra > 0)
            {
                return;
            }
            __result = (int)Unity.Mathematics.math.clamp((long)__result + extra, int.MinValue, int.MaxValue);
        }
    }
}
