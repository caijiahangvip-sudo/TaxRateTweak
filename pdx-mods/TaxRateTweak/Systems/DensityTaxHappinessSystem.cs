using Game;
using Game.City;
using Game.Policies;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TaxRateTweak.Systems
{
    /// <summary>
    /// 把分密度附加税接进原版的"税收"幸福度因素。
    ///
    /// 原版算法（CitizenHappinessSystem.GetTaxBonuses）：每个市民取 `10 − 他那档的名义住宅税率`，
    /// 先经 `CityUtils.ApplyModifier(..., CityModifierType.TaxHappiness)` 修正，再乘该学历档的
    /// 系数，最后按人口比例加权平均。也就是说这个城市修正项是在"10 − 税率"上先加一个常数——
    /// 等价于给所有人的名义税率加上同一个数。
    ///
    /// 于是把该项设为"负的人口加权平均附加税"，就得到"附加税对幸福度的影响"。
    /// 为什么一个标量就够：那个公式对税率是线性的，学历系数只依赖学历、附加税只依赖住房密度，
    /// 两者线性可分，所以 25 格（5 学历 × 5 密度）会塌缩成一个加权平均。这是在"学历与住房密度
    /// 不相关"假设下的精确实现；要按学历分别给不同值，必须改后台 Burst 作业的机器码，不做。
    ///
    /// 难点：城市实体的 CityModifier 缓冲区由游戏的策略流程整块 Clear + 重算
    /// （CityModifierUpdateSystem / ModifiedSystem），所以必须在"重建之后、幸福度读取之前"
    /// 每帧补写。除了这条重建路径，反编译代码里没有任何地方写这个缓冲区，因此可以用
    /// "当前值 ≠ 我们上次写的值 ⇒ 被重建过"来判断，并把当前值当作游戏自己那份，避免叠加。
    /// </summary>
    [UpdateAfter(typeof(CityModifierUpdateSystem))]
    [UpdateBefore(typeof(CitizenHappinessSystem))]
    public partial class DensityTaxHappinessSystem : GameSystemBase
    {
        /// <summary>全城人口加权的平均附加税（分数：0.10 = 10%）。由 DensityTaxSystem 每遍预估/结算写入。</summary>
        public static float AverageSurcharge;
        /// <summary>AverageSurcharge 是否已被填过一次。</summary>
        public static bool AverageSurchargeValid;

        private CitySystem m_CitySystem;
        private CitizenHappinessSystem m_Happiness;
        private float m_Foreign;      // 游戏自己那份 delta.x（我们的贡献之外的部分）
        private float m_LastWritten;  // 我们上次写进缓冲区的 delta.x
        private bool m_HasWritten;
        private float m_LastLoggedDesired = float.NaN;
        private int m_LastErrorTick;
        // 原版五个学历档税收系数的绝对值最大者（只读一次，参数是静态资产数据）。
        private float m_MaxAbsTaxMultiplier;
        private bool m_ParametersRead;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_Happiness = World.GetOrCreateSystemManaged<CitizenHappinessSystem>();
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            // 缓冲区随存档走，句柄与外部写入都可能变，重新建立"游戏那份"的基准。
            m_HasWritten = false;
            m_Foreign = 0f;
            m_LastWritten = 0f;
            m_LastLoggedDesired = float.NaN;
            m_ParametersRead = false; // 世界重建后重新读一遍幸福度参数实体
        }

        protected override void OnUpdate()
        {
            // 与分密度附加税同生共死：关掉附加税即自动撤掉幸福度影响。
            if (!Settings.Enabled || !Settings.EnableDensityTax || !AverageSurchargeValid)
            {
                return;
            }

            try
            {
                Entity city = m_CitySystem.City;
                if (city == Entity.Null || !EntityManager.HasBuffer<CityModifier>(city))
                {
                    return;
                }

                int index = (int)CityModifierType.TaxHappiness;
                DynamicBuffer<CityModifier> modifiers = EntityManager.GetBuffer<CityModifier>(city);
                // 缓冲区长度只到"政策用到的最大的那个类型"为止，注入前先补齐。
                while (modifiers.Length <= index)
                {
                    modifiers.Add(default(CityModifier));
                }

                // 单位：幸福度算的是 `10 − 名义住宅税率`，税率和这个值都是"百分点"（10 = 10%），
                // 所以修正量也必须用百分点。1.8.15/1.8.16 把"分数"直接当百分点传了
                // （0.11 而不是 11），影响只有实际的 1/100——100% 的附加税只让幸福度动了 1 点。
                //
                // 不做任何上限或截断：附加税被等量当作"有效住宅税率的抬升"，走原版同一套公式、
                // 同一灵敏度，于是影响严格与附加税成比例（1.8.17 那道 ±8 点预算会把 50% 和 100% 压成同一个值）。
                // 代价：−1000% 的补贴档会给出极大的幸福度加成——与把顶部税率设到 −1000% 是同一量级，
                // 那本来就是本模组允许的范围；数值只会顶到幸福度上限，不会溢出。
                EnsureParametersRead();
                float shiftPercent = AverageSurcharge * 100f;
                // 税率被抬高了 shift，等价于 (10 − 税率) 减去同样的量。
                float desired = -shiftPercent;

                float current = modifiers[index].m_Delta.x;
                if (!m_HasWritten || current != m_LastWritten)
                {
                    // 第一次进来，或缓冲区刚被政策流程重建过 → 当前值就是游戏自己那份。
                    m_Foreign = current;
                }

                float target = m_Foreign + desired;
                if (current != target)
                {
                    CityModifier entry = modifiers[index];
                    entry.m_Delta.x = target;
                    modifiers[index] = entry;
                }
                m_LastWritten = target;
                m_HasWritten = true;

                if (desired != m_LastLoggedDesired)
                {
                    m_LastLoggedDesired = desired;
                    // 量级换算，便于核对：面板里的税收因素 ≈ (10 − 名义税率 + 修正) × 学历系数 ÷ 2。
                    Mod.log.Info($"分密度附加税已接入税收幸福度：平均附加税 {AverageSurcharge * 100f:0.#}% " +
                        $"→ 等同把有效住宅税率抬升 {-desired:0.##} 个百分点（原版同公式、不做截断；" +
                        $"学历系数最大绝对值 {m_MaxAbsTaxMultiplier:0.####}）。" + ReadTaxFactor());
                }
            }
            catch (System.Exception ex)
            {
                // 每秒最多一条，绝不刷屏。
                int now = System.Environment.TickCount;
                if (unchecked(now - m_LastErrorTick) >= 1000)
                {
                    m_LastErrorTick = now;
                    Mod.log.Warn($"DensityTaxHappinessSystem: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 读一次原版五个学历档的税收系数（静态资产数据），取绝对值最大者，用于反算影响上限。
        /// 只读一次，世界重建后由 OnGameLoaded 复位。
        /// </summary>
        private void EnsureParametersRead()
        {
            if (m_ParametersRead)
            {
                return;
            }
            try
            {
                var query = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.CitizenHappinessParameterData>());
                if (query.IsEmptyIgnoreFilter)
                {
                    return; // 参数实体还没就绪，下帧再试
                }
                var data = query.GetSingleton<Game.Prefabs.CitizenHappinessParameterData>();
                m_MaxAbsTaxMultiplier = math.max(
                    math.max(math.abs(data.m_TaxUneducatedMultiplier), math.abs(data.m_TaxPoorlyEducatedMultiplier)),
                    math.max(
                        math.max(math.abs(data.m_TaxEducatedMultiplier), math.abs(data.m_TaxWellEducatedMultiplier)),
                        math.abs(data.m_TaxHighlyEducatedMultiplier)));
                m_ParametersRead = true;
            }
            catch (System.Exception)
            {
                // 读不到只影响日志里的系数展示（上限逻辑已在 1.8.18 移除），下帧再试。
            }
        }

        /// <summary>
        /// 读一次原版"税收"幸福度因素的现值，作为注入是否被幸福度系统采纳的现场证据：
        /// 修正生效时它会跟着移动；若一直不动，说明注入被政策重建覆盖或在读取之后。
        /// </summary>
        private string ReadTaxFactor()
        {
            try
            {
                var query = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.HappinessFactorParameterData>());
                if (query.IsEmptyIgnoreFilter)
                {
                    return string.Empty;
                }
                Entity parametersEntity = query.GetSingletonEntity();
                var parameters = EntityManager.GetBuffer<HappinessFactorParameterData>(parametersEntity, isReadOnly: true);
                var locked = GetComponentLookup<Game.Prefabs.Locked>(isReadOnly: true);
                float3 factor = m_Happiness.GetHappinessFactor(CitizenHappinessSystem.HappinessFactor.Tax, parameters, ref locked);
                return $"（税收因素现值 {factor.x:0.###}）";
            }
            catch (System.Exception ex)
            {
                return $"（读取税收因素失败：{ex.GetType().Name}）";
            }
        }
    }
}
