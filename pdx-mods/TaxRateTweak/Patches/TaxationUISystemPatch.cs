using Game.Prefabs;
using Game.Simulation;
using HarmonyLib;
using Unity.Mathematics;

namespace TaxRateTweak.Patches
{
    /// <summary>
    /// Best-effort patch to the in-game taxation panel. The panel reads its slider min/max from
    /// the cached <see cref="TaxParameterData"/> limits (via private helpers GetLimits /
    /// GetResourceLimits), NOT from <see cref="TaxSystem.GetTaxRateRange"/>. Without extending
    /// these, the native slider would keep showing the vanilla range even though the simulation
    /// now accepts wider values. Signatures confirmed by reflection against Game.dll.
    /// </summary>
    [HarmonyPatch(typeof(Game.UI.InGame.TaxationUISystem))]
    public static class TaxationUISystemPatch
    {
        /// <summary>Extend the per-area (Residential/Commercial/Industrial/Office) slider range.</summary>
        [HarmonyPatch(typeof(Game.UI.InGame.TaxationUISystem), "GetLimits",
            new[] { typeof(Game.Simulation.TaxAreaType), typeof(Game.Prefabs.TaxParameterData) })]
        [HarmonyPostfix]
        public static void GetLimitsPostfix(ref int2 __result)
        {
            if (Settings.Enabled)
            {
                __result = Settings.GetRange();
            }
        }

        /// <summary>Extend the per-resource slider range (commercial/industrial/office sub-rates).</summary>
        [HarmonyPatch(typeof(Game.UI.InGame.TaxationUISystem), "GetResourceLimits",
            new[] { typeof(Game.Simulation.TaxAreaType), typeof(Game.Prefabs.TaxParameterData) })]
        [HarmonyPostfix]
        public static void GetResourceLimitsPostfix(ref int2 __result)
        {
            if (Settings.Enabled)
            {
                __result = Settings.GetRange();
            }
        }
    }
}
