using System;
using System.Collections.Generic;
using Colossal.UI.Binding;
using Game;
using Game.Simulation;
using Game.UI;
using Game.UI.InGame;
using TaxRateTweak.Services;

namespace TaxRateTweak.Systems
{
    public partial class DensityDemandUISystem : UISystemBase
    {
        private FiveDensityDemandSystem m_Demand;
        private ResidentialDemandSystem m_Vanilla;
        private RawValueBinding m_Binding;
        // 上次推给 UI 的内容签名：需求系统每 16 模拟帧就重算一次并抬高 Revision，但数值按天变化，
        // 无脑推送会让五类需求区块/工具栏/图例反复重绘。
        private long m_Signature = long.MinValue;
        // 写绑定时要输出"启用态"，而 RawValueBinding 的写入委托没有参数，只能靠字段传递。
        private bool m_Active;
        // 复用缓冲区：每次绑定更新都要为 5 个类别建列表并排序，稳态下不该每帧产生垃圾。
        private readonly List<FactorInfo> m_FactorBuffer = new List<FactorInfo>(19);

        public override GameMode gameMode => GameMode.Game;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Demand = World.GetOrCreateSystemManaged<FiveDensityDemandSystem>();
            m_Vanilla = World.GetOrCreateSystemManaged<ResidentialDemandSystem>();
            AddBinding(m_Binding = new RawValueBinding("TaxRateTweak", "densityDemand", WriteDemand));
            Mod.log.Info("TaxRateTweak 1.8.25: five-density UI binding registered.");
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            bool enabled = Mod.FiveDensityReady && Settings.Enabled && Settings.EnableDensityTax;
            if (enabled)
            {
                try
                {
                    if (!m_Demand.Ready) m_Demand.Recalculate(m_Vanilla);
                    m_Demand.RefreshTaxes();
                }
                catch (Exception exception)
                {
                    Mod.FiveDensityReady = false;
                    m_Demand.Invalidate();
                    enabled = false;
                    Mod.log.Error($"Five-density UI initialization failed; reverting to vanilla: {exception}");
                }
            }
            bool active = enabled && m_Demand.Ready;
            // 内容签名不变就不推：需求系统每 16 模拟帧重算一次，但数值是按天变化的，
            // 无脑推送等于每 0.25 秒让 UI 重绘一遍五类需求区块、工具栏需求条与图例。
            long signature = ComputeSignature(active);
            if (signature == m_Signature) return;
            m_Signature = signature;
            m_Active = active;
            m_Binding.Update();
        }

        /// <summary>
        /// 把要推给 UI 的内容（启用态 + 五档数值/解锁态/税率影响 + 各档全部因素）压成一个签名。
        /// 用 64 位 FNV 混合，碰撞概率可忽略；刻意不把 Revision 算进去——它每 16 帧就变。
        /// </summary>
        private long ComputeSignature(bool active)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                hash = (hash ^ (active ? 1u : 0u)) * 1099511628211L;
                for (int category = 0; category < DensityDemand.Count; category++)
                {
                    hash = (hash ^ (uint)m_Demand.Levels.Get(category)) * 1099511628211L;
                    hash = (hash ^ (m_Demand.IsUnlocked(category) ? 1u : 0u)) * 1099511628211L;
                    hash = (hash ^ (uint)m_Demand.TaxEffects[category]) * 1099511628211L;
                    int[] factors = m_Demand.Factors[category];
                    for (int factor = 0; factor < factors.Length; factor++)
                    {
                        hash = (hash ^ (uint)factors[factor]) * 1099511628211L;
                    }
                }
                return hash;
            }
        }

        private void WriteDemand(IJsonWriter writer)
        {
            writer.TypeBegin("TaxRateTweak.DensityDemand");
            writer.PropertyName("enabled");
            writer.Write(m_Active);
            writer.PropertyName("values");
            writer.ArrayBegin(DensityDemand.Count);
            for (int category = 0; category < DensityDemand.Count; category++) writer.Write(m_Demand.Levels.Get(category));
            writer.ArrayEnd();
            writer.PropertyName("unlocked");
            writer.ArrayBegin(DensityDemand.Count);
            for (int category = 0; category < DensityDemand.Count; category++) writer.Write(m_Demand.IsUnlocked(category));
            writer.ArrayEnd();
            writer.PropertyName("factors");
            writer.ArrayBegin(DensityDemand.Count);
            for (int category = 0; category < DensityDemand.Count; category++) WriteFactors(writer, category);
            writer.ArrayEnd();
            writer.TypeEnd();
        }

        private void WriteFactors(IJsonWriter writer, int category)
        {
            var factors = m_FactorBuffer;
            factors.Clear();
            for (int factor = 0; factor < m_Demand.Factors[category].Length; factor++)
            {
                int weight = m_Demand.Factors[category][factor];
                if (weight != 0) factors.Add(new FactorInfo(factor, weight));
            }
            factors.Sort();
            int tax = m_Demand.TaxEffects[category];
            int count = Math.Min(tax == 0 ? 5 : 4, factors.Count);
            writer.ArrayBegin(count + (tax == 0 ? 0 : 1));
            for (int index = 0; index < count; index++) factors[index].WriteDemandFactor(writer);
            if (tax != 0)
            {
                writer.TypeBegin("TaxRateTweak.DensityTaxFactor");
                writer.PropertyName("factor");
                writer.Write("TaxRateTweakDensityTax");
                writer.PropertyName("weight");
                writer.Write(tax);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }
    }
}
