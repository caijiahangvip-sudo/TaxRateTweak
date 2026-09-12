using System;
using Game.Simulation;
using HarmonyLib;
using Unity.Mathematics;

namespace TaxRateTweak.Patches
{
    [HarmonyPatch(typeof(ResidentialDemandSystem), "OnUpdate")]
    public static class ResidentialDemandPatch
    {
        private static readonly AccessTools.FieldRef<ResidentialDemandSystem, int3> DemandRef =
            AccessTools.FieldRefAccess<ResidentialDemandSystem, int3>("m_LastBuildingDemand");

        [HarmonyPrepare]
        public static bool Prepare() => DemandRef != null;

        [HarmonyPostfix]
        public static void Postfix(ResidentialDemandSystem __instance)
        {
            if (!Mod.FiveDensityReady || !Settings.Enabled || !Settings.EnableDensityTax) return;
            Systems.FiveDensityDemandSystem system = null;
            try
            {
                system = __instance.World.GetOrCreateSystemManaged<Systems.FiveDensityDemandSystem>();
                int timingStart = System.Environment.TickCount;
                bool recalculated = system.Recalculate(__instance);
                // 回报耗时：这是每 16 模拟帧一次的全城扫描 + 若干依赖同步点，模组里唯一还没量化的一项。
                system.RecordCallTiming(unchecked(System.Environment.TickCount - timingStart));
                if (recalculated)
                {
                    var levels = system.Levels;
                    DemandRef(__instance) = new int3(levels.Low, math.max(levels.Row, levels.Medium),
                        math.max(levels.High, levels.LowRent));
                }
            }
            catch (Exception exception)
            {
                system?.Invalidate();
                Mod.FiveDensityReady = false;
                Mod.log.Error($"Five-density demand disabled; reverting to vanilla demand: {exception}");
            }
        }
    }
}
