using System;
using Colossal.UI.Binding;
using Game.Simulation;
using Game.UI.InGame;
using HarmonyLib;
using TaxRateTweak.Systems;

namespace TaxRateTweak.Patches
{
    /// <summary>
    /// 把"分密度住宅附加税"直接注入原生 经济→税收 面板：
    /// 在住宅区 5 个学历子行之后追加 5 行（resource index 5-9 = 低/联排/中/高/廉租）。
    ///
    /// 原理：TaxationUISystem 的绑定 delegate 全部指向命名私有方法（方法组），逐点 patch：
    /// - UpdateAreaResources  → 住宅多输出 5 行（前缀，住宅分支整体重写）
    /// - GetResourceTaxRate   → 密度行读 Settings.DensityTax_*（后缀）
    /// - SetResourceTaxRate   → 密度行写 Settings.DensityTax_*（前缀，跳过原生）
    /// - GetEstimatedResourceTaxIncome → 密度行显示实时预估（1024 帧刷新一次，同原生口径）
    /// - UpdateResourceInfo   → 密度行换用各密度专属图标（后缀）
    ///
    /// 行标签来自本地化 EconomyPanel.TAXATION_RESIDENTIAL_SLIDER_JOBLEVEL:5-9（Mod.OnLoad 注入）。
    /// 行范围走既有 TaxationUISystemPatch 扩好的 -1000%..+100%，无需再动。
    /// </summary>
    [HarmonyPatch]
    public static class DensityTaxPanelPatch
    {
        // 三处面板绑定 + 一处税率绑定都走"惰性生成的直接字段访问委托"。
        // 绝不能写成静态初始化器：任一字段改名都会让类型初始化抛异常，Harmony 的
        // PatchAll 连带失败，本类的六处面板集成会一起消失（1.8.7 用户看到的就是这个现场）。
        private static AccessTools.FieldRef<TaxationUISystem, GetterMapBinding<TaxResource, int>> s_IncomeBindings;
        private static bool s_IncomeBindingsTried;
        private static AccessTools.FieldRef<TaxationUISystem, GetterMapBinding<int, int>> s_AreaIncomeBindings;
        private static bool s_AreaIncomeBindingsTried;
        private static AccessTools.FieldRef<TaxationUISystem, GetterValueBinding<int>> s_TotalIncomeBinding;
        private static bool s_TotalIncomeBindingTried;
        private static AccessTools.FieldRef<TaxationUISystem, GetterMapBinding<TaxResource, int>> s_ResourceTaxRates;
        private static bool s_ResourceTaxRatesTried;

        private static AccessTools.FieldRef<TaxationUISystem, GetterMapBinding<TaxResource, int>> IncomeBindings
        {
            get
            {
                if (!s_IncomeBindingsTried)
                {
                    s_IncomeBindingsTried = true;
                    try
                    {
                        s_IncomeBindings = AccessTools.FieldRefAccess<TaxationUISystem, GetterMapBinding<TaxResource, int>>("m_ResourceTaxIncomes");
                    }
                    catch (Exception e)
                    {
                        s_IncomeBindings = null;
                        Mod.log.Warn($"TaxRateTweak: m_ResourceTaxIncomes 不可用，密度行预估不再主动推送（原生仍会刷新）: {e.Message}");
                    }
                }
                return s_IncomeBindings;
            }
        }

        private static AccessTools.FieldRef<TaxationUISystem, GetterMapBinding<int, int>> AreaIncomeBindings
        {
            get
            {
                if (!s_AreaIncomeBindingsTried)
                {
                    s_AreaIncomeBindingsTried = true;
                    try
                    {
                        s_AreaIncomeBindings = AccessTools.FieldRefAccess<TaxationUISystem, GetterMapBinding<int, int>>("m_AreaTaxIncomes");
                    }
                    catch (Exception e)
                    {
                        s_AreaIncomeBindings = null;
                        Mod.log.Warn($"TaxRateTweak: m_AreaTaxIncomes 不可用，住宅小计不再主动推送（原生仍会刷新）: {e.Message}");
                    }
                }
                return s_AreaIncomeBindings;
            }
        }

        private static AccessTools.FieldRef<TaxationUISystem, GetterValueBinding<int>> TotalIncomeBinding
        {
            get
            {
                if (!s_TotalIncomeBindingTried)
                {
                    s_TotalIncomeBindingTried = true;
                    try
                    {
                        s_TotalIncomeBinding = AccessTools.FieldRefAccess<TaxationUISystem, GetterValueBinding<int>>("m_TaxIncome");
                    }
                    catch (Exception e)
                    {
                        s_TotalIncomeBinding = null;
                        Mod.log.Warn($"TaxRateTweak: m_TaxIncome 不可用，税收总计不再主动推送（原生仍会刷新）: {e.Message}");
                    }
                }
                return s_TotalIncomeBinding;
            }
        }

        /// <summary>税率表绑定（刷新滑块回显）。原来是每次事件都 Traverse.Create 反射取字段。</summary>
        private static AccessTools.FieldRef<TaxationUISystem, GetterMapBinding<TaxResource, int>> ResourceTaxRates
        {
            get
            {
                if (!s_ResourceTaxRatesTried)
                {
                    s_ResourceTaxRatesTried = true;
                    try
                    {
                        s_ResourceTaxRates = AccessTools.FieldRefAccess<TaxationUISystem, GetterMapBinding<TaxResource, int>>("m_ResourceTaxRates");
                    }
                    catch (Exception e)
                    {
                        s_ResourceTaxRates = null;
                        Mod.log.Warn($"TaxRateTweak: m_ResourceTaxRates 不可用，密度行滑块回显可能滞后: {e.Message}");
                    }
                }
                return s_ResourceTaxRates;
            }
        }

        public static void RefreshEstimateBindings(TaxationUISystem system)
        {
            var incomes = IncomeBindings;
            if (incomes != null)
            {
                incomes(system)?.UpdateAll();
            }
            var areaIncomes = AreaIncomeBindings;
            if (areaIncomes != null)
            {
                areaIncomes(system)?.UpdateAll();
            }
            var total = TotalIncomeBinding;
            if (total != null)
            {
                total(system)?.Update();
            }
        }

        private const int DensityRowStart = 5; // 密度行 resource 起始索引（0-4 是学历行）
        private const int DensityRowCount = 5; // 低/联排/中/高/廉租

        private static readonly string[] DensityIcons =
        {
            "Media/Game/Icons/ZoneResidentialLow.svg",
            "Media/Game/Icons/ZoneResidentialMediumRow.svg",
            "Media/Game/Icons/ZoneResidentialMedium.svg",
            "Media/Game/Icons/ZoneResidentialHigh.svg",
            "Media/Game/Icons/ZoneResidentialLowRent.svg",
        };

        private static bool IsDensityRow(int areaType, int resource)
        {
            return Settings.Enabled && Settings.EnableDensityTax
                && areaType == 1
                && resource >= DensityRowStart
                && resource < DensityRowStart + DensityRowCount;
        }

        [HarmonyPatch(typeof(TaxationUISystem), "SetAreaTaxRate")]
        [HarmonyPostfix]
        private static void SetAreaTaxRatePostfix(TaxationUISystem __instance, int areaType)
        {
            if (areaType != (int)TaxAreaType.Residential || !Settings.Enabled || !Settings.EnableDensityTax)
            {
                return;
            }
            // 用缓存的各档应税基数直接重算（5 次乘法）。绝不在这里扫描全城：拖动滑块会连发事件，
            // 一次扫描几十毫秒，每帧扫一遍就是帧率暴降的根源（1.8.16 税基改成按人累加后更重）。
            // 缓存缺失（刚读档 / 刚启用）时才退回完整扫描。
            if (DensityTaxSystem.RecomputeFromCachedBasis())
            {
                RefreshEstimateBindings(__instance);
            }
            else
            {
                __instance.World.GetOrCreateSystemManaged<DensityEstimateSystem>().InvalidateRefresh();
            }
        }

        /// <summary>areaResources：住宅区输出 10 行（5 学历 + 5 密度），其它区走原生。</summary>
        [HarmonyPatch(typeof(TaxationUISystem), "UpdateAreaResources")]
        [HarmonyPrefix]
        public static bool UpdateAreaResourcesPrefix(IJsonWriter binder, int area)
        {
            if (!Settings.Enabled || !Settings.EnableDensityTax || (byte)area != 1)
            {
                return true;
            }

            binder.ArrayBegin((uint)(5 + DensityRowCount));
            for (int i = 0; i < 5 + DensityRowCount; i++)
            {
                binder.Write(new TaxResource { m_Resource = i, m_AreaType = 1 });
            }
            binder.ArrayEnd();
            return false;
        }

        /// <summary>
        /// resourceTaxRates：密度行显示"附加税率"本身，不是"基础 + 附加"的合计。
        /// 用合计口径会让行数字说 10%（那是基础住宅税率）而模组实际收 0%，玩家据此以为
        /// 有附加税——1.8.7–1.8.12 的现场。只显示附加税率后：行数字 = 模组实际征收的税率
        /// （与预估列同口径），该档住户的合计住宅税率 = 顶部住宅税率 + 本行数字。
        /// </summary>
        [HarmonyPatch(typeof(TaxationUISystem), "GetResourceTaxRate")]
        [HarmonyPostfix]
        public static void GetResourceTaxRatePostfix(TaxationUISystem __instance, TaxAreaType type, int resource, ref int __result)
        {
            if (IsDensityRow((int)type, resource))
            {
                __result = Settings.GetDensityRate(resource - DensityRowStart);
            }
        }

        /// <summary>setResourceTaxRate：密度行写入设置并刷新该行绑定，不碰 TaxSystem。</summary>
        [HarmonyPatch(typeof(TaxationUISystem), "SetResourceTaxRate")]
        [HarmonyPrefix]
        public static bool SetResourceTaxRatePrefix(TaxationUISystem __instance, int resource, int areaType, int rate)
        {
            if (!IsDensityRow(areaType, resource))
            {
                return true;
            }

            // 存的就是本行显示的附加税率本身（显示什么就收什么），
            // 不再折算成"相对基础税率的差值"。
            Settings.SetDensityRate(resource - DensityRowStart, Settings.ClampRate(rate));
            Settings.RequestSave();

            // 同顶部税率：用缓存基数直接重算（5 次乘法），不扫描全城。
            if (DensityTaxSystem.RecomputeFromCachedBasis())
            {
                RefreshEstimateBindings(__instance);
            }
            else
            {
                __instance.World.GetOrCreateSystemManaged<DensityEstimateSystem>().InvalidateRefresh();
            }

            // 照原生 trigger 的尾部行为：立即刷新该行的税率绑定，让滑块回显新值（开销极小）。
            var rates = ResourceTaxRates;
            if (rates != null)
            {
                rates(__instance)?.Update(new TaxResource { m_AreaType = 1, m_Resource = resource });
            }
            return false;
        }

        /// <summary>resourceTaxIncomes：密度行显示实时预估（正=净收入，负=净补贴）。</summary>
        [HarmonyPatch(typeof(TaxationUISystem), "GetEstimatedResourceTaxIncome")]
        [HarmonyPostfix]
        public static void GetEstimatedResourceTaxIncomePostfix(TaxAreaType type, int resource, ref int __result)
        {
            if (IsDensityRow((int)type, resource))
            {
                __result = DensityTaxSystem.GetLastSettled(resource - DensityRowStart);
            }
        }

        /// <summary>taxResourceInfos：密度行换用各密度专属图标（标签仍走索引本地化）。</summary>
        [HarmonyPatch(typeof(TaxationUISystem), "UpdateResourceInfo")]
        [HarmonyPostfix]
        public static void UpdateResourceInfoPostfix(TaxResource resource, ref TaxResourceInfo __result)
        {
            if (IsDensityRow(resource.m_AreaType, resource.m_Resource))
            {
                __result.m_Icon = DensityIcons[resource.m_Resource - DensityRowStart];
            }
        }
    }
}
