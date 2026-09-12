using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using TaxRateTweak.Services;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace TaxRateTweak.Systems
{
    /// <summary>
    /// 分密度住宅附加税结算系统：按住宅建筑所属 zone 的密度档位，对住户的钱
    /// （Game.Economy.Resources 缓冲区里的 Resource.Money）做真实增减（正税率=征收，负=补贴），
    /// 并把等额金额加进城市金库（PlayerMoney）+ 记进城市统计（Income/TaxResidential），
    /// 保证钱既不凭空产生也不凭空消失，预算面板的住宅税收条目可见。
    ///
    /// 结算节奏（1.5.0 起）：每游戏日一次（262144 模拟帧 = 1 游戏日，与 CalendarEventLaunchSystem
    /// 的 UPDATES_PER_DAY=4 × 65536 推得一致），金额 = (昨日工资 - 住宅免税线) × 附加税率% 。
    /// 旧版"每 N 帧结算、金额÷30"的实际税负与帧间隔设置耦合（名义税率的约 8.5 倍/日），已废弃。
    ///
    /// 征收范围（1.8.11 起）：只收"附加"部分。基础住宅税率由原版 TaxSystem.PayTax 从
    /// 同一税基（未税收入）直接扣住户钱包，本系统若把基础税率也算进金额，同一笔基础税会被
    /// 重复征收一次（1.8.7–1.8.10 的缺陷）。附加税率为 0 的档位整档跳过。
    ///
    /// 链路：住户 → PropertyRenter.m_Property → PrefabRef → SpawnableBuildingData.m_ZonePrefab
    /// → ZoneDensityCatalog 密度档位 → 附加税率。
    ///
    /// 设置按存档持久化（IDefaultSerializable）：每个存档绑定自己的税率，互不污染；
    /// 读档后同步回写 settings.txt 保持一致，新城市用 settings.txt 的全局值起步。
    /// </summary>
    public partial class DensityTaxSystem : GameSystemBase, IDefaultSerializable, ISerializable
    {
        // 262144 模拟帧 = 1 游戏日。
        public const uint kFramesPerDay = 262144u;

        // 序列化版本：4 = 当前（纯语义标记，字段布局与 3 完全相同）。2 及更早缺福利开关字段。
        //
        // 刻意不做"绝对税率 → 相对差值"的数值迁移：版本号 3 既可能是 1.8.7 之前写的
        // （那时 DensityTax_* 被当作整档税率收），也可能是 1.8.7–1.8.10 写的（那时已经是相对
        // 基础税率的差值），两者无法区分，统一减去基础税率会把后者再减一次。
        // 直接按"相对基础税率的差值"解释反而是自洽的：面板行显示 base + 差值 = 该档住户的
        // 合计税率（基础部分由原版 TaxSystem.PayTax 从同一税基收，附加部分由本系统收），
        // 而本系统收的金额与 1.8.7 之前完全一致（只收 DensityTax_* 本身），
        // 既不会凭空多收，也不会给整档住户凭空发补贴。
        private const int kSaveVersion = 4;

        // 各密度档的预估/实收总额（0=低 1=联排 2=中 3=高 4=廉租），供税收面板"预估"列显示。
        // 由 DensityEstimateSystem（UIUpdate 阶段，暂停时也跑）每秒空转刷新。
        private static readonly int[] s_LastSettled = new int[5];

        /// <summary>上次结算某密度档的实收金额（正=城市净收入，负=净补贴）。</summary>
        public static int GetLastSettled(int densityIndex)
        {
            return (uint)densityIndex < 5u ? s_LastSettled[densityIndex] : 0;
        }

        // 各密度档的"应税基数合计"（Σ 每户按人累加的应税收入）与住户数。它们只依赖住户收入与
        // 免税线，与税率无关，所以拖动税率滑块时可以拿它们直接重算（5 次乘法），不必再扫描全城——
        // 那个扫描现在是几十毫秒一遍，每动一下滑块扫一遍就是掉帧的根源。
        private static readonly long[] s_LastBasis = new long[5];
        private static readonly int[] s_LastCounts = new int[5];
        private static bool s_BasisValid;

        /// <summary>
        /// 用缓存的各档应税基数/住户数 + 当前税率重算预估与幸福度注入量。返回 false = 还没有缓存
        /// （刚读档/刚启用），调用方应退回完整扫描一次。
        /// 注意：与逐户结算相比这里少了每户整数截断，差异在几十万分之一量级，面板上看不出来。
        /// </summary>
        public static bool RecomputeFromCachedBasis()
        {
            if (!s_BasisValid)
            {
                return false;
            }

            long weightedSurcharge = 0;
            long weightedHouseholds = 0;
            for (int index = 0; index < s_LastSettled.Length; index++)
            {
                int rate = ZoneDensityCatalog.GetRateForCategory((DensityCategory)(index + 1));
                long amount = s_LastBasis[index] * rate / 100;
                s_LastSettled[index] = (int)Unity.Mathematics.math.clamp(amount, int.MinValue, int.MaxValue);
                weightedSurcharge += (long)rate * s_LastCounts[index];
                weightedHouseholds += s_LastCounts[index];
            }

            // 幸福度注入用的"人口加权平均附加税"也只依赖各档税率与住户数，所以在这里一起更新，
            // 拖税率时幸福度影响就会立刻跟上，不必等下一次全城扫描。
            if (weightedHouseholds > 0)
            {
                DensityTaxHappinessSystem.AverageSurcharge = weightedSurcharge / (100f * weightedHouseholds);
                DensityTaxHappinessSystem.AverageSurchargeValid = true;
            }
            return true;
        }

        private EntityQuery m_HouseholdQuery;
        private EntityQuery m_EconomyQuery;
        private ZoneDensityCatalog m_Catalog;
        private CitySystem m_CitySystem;
        private CityStatisticsSystem m_CityStatisticsSystem;
        private uint m_LastSettledDay;
        private bool m_DayInitialized;
        private SimulationSystem m_SimulationSystem;
        // 单户结算异常只报一次，避免日志刷屏（下一游戏日重置）。
        private bool m_SettleErrorLogged;
        // 读档后首遍结算的诊断日志只打一次。
        private bool m_DiagLogged;
        // 读档后首日结算的耗时日志只打一次。
        private bool m_SettleTimingLogged;

        // 经济参数实体缓存：每帧解析一次要 CreateEntityQuery + ToEntityArray，缓存后只做存在性校验。
        private Entity m_EconomyEntity = Entity.Null;
        // 游戏自己的免税线值（第一次观察到"不是本模组写的"值时记下），关闭政策时用它还原。
        private int m_VanillaMinimumEarnings;
        private bool m_VanillaMinimumEarningsValid;
        // 本模组最近一次写到经济参数上的免税线值；用它区分组件上的值出自谁。
        private int m_LastPushedMinimumEarnings = int.MinValue;
        // 免税线写入校验只做一次；期望值与实际值不一致时打 WARN。
        private bool m_MinimumEarningsVerified;
        // 免税线生效值变更日志：只在目标值真的变化时打一条，避免每帧刷屏。
        private int m_LoggedMinimumEarnings = int.MinValue;
        // 免税线读写异常只记一次。
        private bool m_MinimumEarningsErrorLogged;

        /// <summary>
        /// 空转预估入口（供 DensityEstimateSystem 在 UIUpdate 阶段调用——暂停时也要能刷新预估）。
        /// 返回 false = 世界未就绪，调用方稍后重试。
        /// </summary>
        public bool RunEstimate()
        {
            return Settle(estimateOnly: true);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Catalog = new ZoneDensityCatalog(World);
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_CityStatisticsSystem = World.GetOrCreateSystemManaged<CityStatisticsSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_HouseholdQuery = GetEntityQuery(
                ComponentType.ReadOnly<Household>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.ReadWrite<Game.Economy.Resources>());
            m_EconomyQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
        }

        protected override void OnUpdate()
        {
            // 设置存盘防抖检查（每帧，开销可忽略）。
            Settings.FlushSaveIfNeeded();

            try
            {
                if (!Settings.Enabled)
                {
                    return;
                }

                // 住宅免税线覆盖（全局经济参数，独立于密度税开关）。
                ApplyMinimumEarningsOverride();

                if (!Settings.EnableDensityTax)
                {
                    return;
                }

                // 每游戏日结算一次（与帧率、时间流速、间隔设置全部解耦）。
                uint day = m_SimulationSystem.frameIndex / kFramesPerDay;
                if (!m_DayInitialized)
                {
                    m_DayInitialized = true;
                    m_LastSettledDay = day;
                    return; // 首日不回溯结算，等下一个日界。
                }
                if (day == m_LastSettledDay)
                {
                    return;
                }
                m_LastSettledDay = day;
                m_SettleErrorLogged = false;

                int settleTick = System.Environment.TickCount;
                Settle();
                if (!m_SettleTimingLogged)
                {
                    m_SettleTimingLogged = true;
                    Mod.log.Info($"密度税日结算耗时 {System.Environment.TickCount - settleTick}ms。");
                }
            }
            catch (System.Exception ex)
            {
                // 绝不崩游戏；仅在调试模式下记录。
                if (Settings.DebugLogging)
                {
                    Mod.log.Warn($"DensityTaxSystem.OnUpdate failed: {ex}");
                }
            }
        }

        /// <summary>读档/换世界后必须调用：zone prefab 实体句柄已重映射，缓存目录全部失效。</summary>
        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_Catalog.Invalidate();
            m_DayInitialized = false;
            m_DiagLogged = false;
            m_SettleTimingLogged = false;
            // s_LastSettled 是 static：不清零的话换存档瞬间面板会显示上一个存档的预估金额。
            for (int index = 0; index < s_LastSettled.Length; index++)
            {
                s_LastSettled[index] = 0;
            }
            // 经济参数实体句柄可能随世界重建失效，交给 ResolveEconomyEntity 重新解析；
            // 写值/校验状态一并重置，让新存档重新记录原版免税线。
            m_EconomyEntity = Entity.Null;
            m_LastPushedMinimumEarnings = int.MinValue;
            m_MinimumEarningsVerified = false;
            if (Settings.DebugLogging)
            {
                Mod.log.Info("DensityTaxSystem: 存档加载，密度目录已失效待重建。");
            }
        }

        /// <summary>
        /// 把 Settings.ResidentialMinimumEarnings 推进运行时经济参数实体（每帧检查，值不同才写）。
        /// 免税线是原版参数：原版 PayWageSystem / EconomyUtils 按"(工资 − 免税线) × 税率"征税，
        /// 所以它既影响原版基础住宅税，也是本模组附加税税基里减掉的那一段。
        ///
        /// 三种状态：Settings >= 0 → 写入该值；Settings &lt; 0 且记过原版值 → 还原原版值（关闭政策
        /// 必须真的撤销覆盖，否则免税线会一直生效到下次读档）；都不成立 → 什么都不做。
        /// </summary>
        private void ApplyMinimumEarningsOverride()
        {
            Entity economyEntity = ResolveEconomyEntity();
            if (economyEntity == Entity.Null)
            {
                return; // 经济参数还没就绪（读档早期），下帧再试
            }

            EconomyParameterData data;
            try
            {
                data = EntityManager.GetComponentData<EconomyParameterData>(economyEntity);
            }
            catch (System.Exception ex)
            {
                LogMinimumEarningsError("读取", ex);
                return;
            }

            // 组件上的值不是我们写进去的 → 这是游戏自己的值，留作"原版值"。
            // 首次观察发生在读档后游戏写完模式数据之后，所以拿到的是真正的原版免税线。
            if (data.m_ResidentialMinimumEarnings != m_LastPushedMinimumEarnings)
            {
                m_VanillaMinimumEarnings = data.m_ResidentialMinimumEarnings;
                m_VanillaMinimumEarningsValid = true;
            }

            int desired = Settings.ResidentialMinimumEarnings >= 0
                ? Settings.ResidentialMinimumEarnings
                : (m_VanillaMinimumEarningsValid ? m_VanillaMinimumEarnings : data.m_ResidentialMinimumEarnings);

            if (data.m_ResidentialMinimumEarnings == desired)
            {
                m_LastPushedMinimumEarnings = desired;
                return;
            }

            data.m_ResidentialMinimumEarnings = desired;
            try
            {
                EntityManager.SetComponentData(economyEntity, data);
            }
            catch (System.Exception ex)
            {
                LogMinimumEarningsError("写入", ex);
                return;
            }
            m_LastPushedMinimumEarnings = desired;

            if (desired != m_LoggedMinimumEarnings)
            {
                m_LoggedMinimumEarnings = desired;
                Mod.log.Info(Settings.ResidentialMinimumEarnings >= 0
                    ? $"住宅免税线已生效：{desired}（原版 {(m_VanillaMinimumEarningsValid ? m_VanillaMinimumEarnings : 0)}）。低于该值的家庭不缴附加税。"
                    : $"住宅免税线已还原为原版值：{desired}。");
            }

            // 只校验一次：确认写入真的落地。ECS 组件写入失败不会抛异常，只会静默不生效——
            // 这正是"免税线好像完全没用"最难排查的那种情况，必须留下证据。
            if (!m_MinimumEarningsVerified)
            {
                m_MinimumEarningsVerified = true;
                try
                {
                    var check = EntityManager.GetComponentData<EconomyParameterData>(economyEntity);
                    if (check.m_ResidentialMinimumEarnings != desired)
                    {
                        Mod.log.Warn($"住宅免税线写入未生效：期望 {desired}，实际 {check.m_ResidentialMinimumEarnings}。" +
                            "若一直如此，说明经济参数被其它系统覆盖，免税线不会影响税基。");
                    }
                    else
                    {
                        Mod.log.Info($"住宅免税线写入校验通过：{desired}。");
                    }
                }
                catch (System.Exception ex)
                {
                    LogMinimumEarningsError("校验", ex);
                }
            }
        }

        /// <summary>经济参数实体解析（带缓存）；返回 Entity.Null = 世界还没就绪。</summary>
        private Entity ResolveEconomyEntity()
        {
            if (m_EconomyEntity != Entity.Null &&
                EntityManager.Exists(m_EconomyEntity) &&
                EntityManager.HasComponent<EconomyParameterData>(m_EconomyEntity))
            {
                return m_EconomyEntity;
            }
            m_EconomyEntity = FindEconomyEntity();
            return m_EconomyEntity;
        }

        /// <summary>
        /// 手动枚举经济参数实体（不用 TryGetSingletonEntity / GetSingleton：托管组件查询的
        /// singleton 快路径在本 ECS 分支上不可靠）。优先取运行时实体，跳过带 PrefabData 的 prefab 实体。
        /// </summary>
        private Entity FindEconomyEntity()
        {
            try
            {
                using (var econEntities = m_EconomyQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < econEntities.Length; i++)
                    {
                        if (!EntityManager.HasComponent<Game.Prefabs.PrefabData>(econEntities[i]))
                        {
                            return econEntities[i];
                        }
                    }
                    // 全是 prefab 实体时退化取第一个（prefab 实体上的参数同样是权威值）。
                    return econEntities.Length > 0 ? econEntities[0] : Entity.Null;
                }
            }
            catch (System.Exception)
            {
                return Entity.Null;
            }
        }

        private void LogMinimumEarningsError(string action, System.Exception ex)
        {
            if (m_MinimumEarningsErrorLogged)
            {
                return;
            }
            m_MinimumEarningsErrorLogged = true;
            Mod.log.Warn($"DensityTaxSystem: 住宅免税线{action}失败，免税线不会生效: {ex.GetType().Name}: {ex.Message}");
        }

        /// <summary>
        /// 按人累加该户的应税收入：(个人收入 − 免税线) 的正部分求和。
        /// 与原版同口径——有工作者按该学历档工资，儿童/青少年按家庭津贴、老人按养老金、
        /// 其余按失业救济（原版这些也都参与免征扣除）。
        /// 两处刻意不判：死亡市民（原版会跳过）与失业补助的领取天数上限；影响仅限当天的微小偏差。
        /// 拿不到成员表时退回按户口径，不至于比之前更差。
        /// </summary>
        private int ComputeTaxableBasis(Entity household, int fallbackHouseholdSalary, int minEarnings,
            in EconomyParameterData economyParams,
            BufferLookup<HouseholdCitizen> citizenBuffers,
            ComponentLookup<Worker> workers,
            ComponentLookup<Citizen> citizens)
        {
            if (household == Entity.Null ||
                !citizenBuffers.TryGetBuffer(household, out DynamicBuffer<HouseholdCitizen> members))
            {
                return fallbackHouseholdSalary - minEarnings;
            }

            int basis = 0;
            for (int index = 0; index < members.Length; index++)
            {
                Entity citizen = members[index].m_Citizen;
                int income;
                if (workers.TryGetComponent(citizen, out Worker worker))
                {
                    income = economyParams.GetWage(worker.m_Level);
                }
                else if (citizens.TryGetComponent(citizen, out Citizen data))
                {
                    switch (data.GetAge())
                    {
                        case CitizenAge.Child:
                        case CitizenAge.Teen:
                            income = economyParams.m_FamilyAllowance;
                            break;
                        case CitizenAge.Elderly:
                            income = economyParams.m_Pension;
                            break;
                        default:
                            income = economyParams.m_UnemploymentBenefit;
                            break;
                    }
                }
                else
                {
                    continue;
                }

                int surplus = income - minEarnings;
                if (surplus > 0)
                {
                    basis += surplus;
                }
            }
            return basis;
        }

        /// <summary>
        /// 逐户结算（estimateOnly=false）或空转预估（estimateOnly=true：只填 s_LastSettled，
        /// 不动钱包、不动金库、不记统计）。返回 false 表示世界还没就绪，调用方应下帧重试。
        /// </summary>
        private bool Settle(bool estimateOnly = false)
        {
            // 五档附加税全为 0（默认态）：本系统无事可做，基础住宅税由原版 TaxSystem.PayTax 征收。
            // 提前返回，连 5 万户的户表拷贝都省掉——暂停时预估每秒跑一遍，这个拷贝不是免费的。
            if (Settings.DensityTax_Low == 0 && Settings.DensityTax_Row == 0 &&
                Settings.DensityTax_Medium == 0 && Settings.DensityTax_High == 0 &&
                Settings.DensityTax_LowRent == 0)
            {
                for (int index = 0; index < s_LastSettled.Length; index++)
                {
                    s_LastSettled[index] = 0;
                }
                if (!m_DiagLogged)
                {
                    m_DiagLogged = true;
                    Mod.log.Info("密度税诊断：五档附加税全为 0 → 本模组不征收任何附加税，五行预估恒为 0。" +
                        "住宅基础税仍由原版 TaxSystem 征收；把某一行调到非 0 才会开始收该档附加税。");
                }
                // 没有附加税 → 幸福度影响归零（但仍标记有效，让注入系统把修正清掉）。
                DensityTaxHappinessSystem.AverageSurcharge = 0f;
                DensityTaxHappinessSystem.AverageSurchargeValid = true;
                return true;
            }

            m_Catalog.EnsureBuilt();
            if (m_Catalog.Count == 0)
            {
                return false; // zone prefab 尚未就绪，本轮跳过。
            }

            // 经济参数（住宅免税线）；没拿到就跳过本轮。
            // 读写一律用 GetComponentData/SetComponentData——与原版 EconomyParametersMode.ApplyModeData 相同。
            // （1.6.5 前用的 GetComponentObject 是旧版 ECS API，在此分支上会抛 IndexOutOfRange。）
            Entity economyEntity = ResolveEconomyEntity();
            if (economyEntity == Entity.Null)
            {
                return false;
            }
            EconomyParameterData economyParams;
            try
            {
                economyParams = EntityManager.GetComponentData<EconomyParameterData>(economyEntity);
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn($"DensityTaxSystem: EconomyParameterData not ready, settle skipped. {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return false;
            }

            // 税基用的免税线只认一个来源：Settings >= 0 时就是被推到经济参数上的那个值。
            // 预估与实收必须走同一条判断，否则面板预估和实际扣款会悄悄分叉。
            int minEarnings = Settings.ResidentialMinimumEarnings >= 0
                ? Settings.ResidentialMinimumEarnings
                : economyParams.m_ResidentialMinimumEarnings;

            var prefabRefLookup = GetComponentLookup<PrefabRef>(true);
            var spawnableLookup = GetComponentLookup<SpawnableBuildingData>(true);
            // 按人算税基要用的只读查询。刻意不做 CompleteDependencyBeforeRO：
            // Worker / Citizen 的写入都是低频的结构性变更，读到的旧值最多影响一天的税基；
            // 而同步点会阻塞在市民更新作业上——宁可数字略偏，也不要停顿。
            var citizenBuffers = GetBufferLookup<HouseholdCitizen>(true);
            var workerLookup = GetComponentLookup<Worker>(true);
            var citizenLookup = GetComponentLookup<Citizen>(true);
            // 空转预估不碰钱包，只读获取；仅真实结算申请写权限。
            var resourcesLookup = GetBufferLookup<Game.Economy.Resources>(estimateOnly);

            var categoryTotals = new long[s_LastSettled.Length];
            long total = 0;
            long totalPositive = 0;
            long totalNegative = 0; // ≤ 0
            int failures = 0;

            // 诊断计数（每次读档后首遍预估打一条 INFO，定位"恒为 0"卡在哪一环）。
            int dScanned = 0, dNoProperty = 0, dNoPrefab = 0, dNoSpawnable = 0,
                dUnknownZone = 0, dZeroSurcharge = 0, dBasisZero = 0, dAmountZero = 0, dSettled = 0;

            // 各密度档的住户数：供幸福度注入算"人口加权平均附加税"。附加税按档是常数，
            // 所以权重只需要各档的住户数（这也是"25 格塌缩成一个数"里那个加权）。
            var householdsByCategory = new int[5];
            // 各档应税基数合计（本遍扫描结果），扫完写入 s_LastBasis 供拖动税率时免扫描重算。
            var basisTotals = new long[5];

            using (var entities = m_HouseholdQuery.ToEntityArray(Allocator.Temp))
            using (var renters = m_HouseholdQuery.ToComponentDataArray<PropertyRenter>(Allocator.Temp))
            using (var households = m_HouseholdQuery.ToComponentDataArray<Household>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    dScanned++;
                    try
                    {
                        Entity property = renters[i].m_Property;
                        if (property == Entity.Null)
                        {
                            dNoProperty++;
                            continue;
                        }

                        PrefabRef prefabRef;
                        if (!prefabRefLookup.TryGetComponent(property, out prefabRef))
                        {
                            dNoPrefab++;
                            continue;
                        }

                        SpawnableBuildingData spawnable;
                        if (!spawnableLookup.TryGetComponent(prefabRef.m_Prefab, out spawnable))
                        {
                            dNoSpawnable++;
                            continue;
                        }

                        var category = m_Catalog.GetCategory(spawnable.m_ZonePrefab);
                        if (category == DensityCategory.Unknown)
                        {
                            dUnknownZone++;
                            continue;
                        }
                        householdsByCategory[(int)category - 1]++;

                        // 附加税率为 0 的档位整档跳过：基础住宅税率由原版 TaxSystem.PayTax
                        // 从同一税基直接扣住户钱包，本系统只收附加部分。把基础税率也算进金额
                        // 会让同一笔基础税被收两次（1.8.7–1.8.10 的缺陷）。
                        int surcharge = ZoneDensityCatalog.GetRateForCategory(category);
                        if (surcharge == 0)
                        {
                            dZeroSurcharge++;
                            continue;
                        }

                        // 税基：**按人**累加 (个人收入 − 免税线) 的正部分，下限 0。
                        // 原版的免征本来就是按人算的（PayWageSystem 对每个市民各减一次免税线），
                        // 幸福度也是按人算的，所以这里必须同口径；按户只减一次会让多职工家庭多缴。
                        // 免税线只免不补：低于线的部分计 0，既不收也不发。
                        // （此前按"户收入 − 免税线"发补贴是错的：免税线能设到 50000，而住户日均
                        // 收入只有几千，等于按"免税线 × 附加率"给全城发钱，金库会被瞬间掏空。）
                        int basis = ComputeTaxableBasis(entities[i], households[i].m_SalaryLastDay, minEarnings,
                            in economyParams, citizenBuffers, workerLookup, citizenLookup);
                        if (basis <= 0)
                        {
                            dBasisZero++;
                            continue;
                        }
                        basisTotals[(int)category - 1] += basis;

                        // 每游戏日一次：金额 = 税基 × 附加税率% 。long 中间值防溢出。
                        // 附加税率为负（玩家主动设的补贴档）时金额为负 = 城市给住户发钱，
                        // 那是显式设置的行为，与免税线无关。
                        int amount = (int)((long)basis * surcharge / 100);
                        if (amount == 0)
                        {
                            dAmountZero++;
                            continue;
                        }
                        dSettled++;

                        // 从住户的钱包扣/补（Money 缓冲区，负 amount 时表现为补贴入帐）。
                        // 空转预估模式跳过一切资金/统计副作用，只累计下方台账。
                        if (!estimateOnly)
                        {
                            DynamicBuffer<Game.Economy.Resources> wallet;
                            if (!resourcesLookup.TryGetBuffer(entities[i], out wallet))
                            {
                                continue;
                            }
                            EconomyUtils.AddResources(Resource.Money, -amount, wallet);
                        }

                        categoryTotals[(int)category - 1] += amount;
                        total += amount;
                        if (amount > 0)
                        {
                            totalPositive += amount;
                        }
                        else
                        {
                            totalNegative += amount; // 负数累加
                        }
                    }
                    catch (System.Exception ex)
                    {
                        // 单户结算失败不影响其余住户；首例记日志供排查（空吞 = 现场无法排查）。
                        failures++;
                        if (!m_SettleErrorLogged)
                        {
                            m_SettleErrorLogged = true;
                            Mod.log.Warn($"DensityTaxSystem: 单户结算失败（本次结算共 {failures}+ 户异常，后续静默）: {ex.Message}");
                        }
                    }
                }
            }

            // 幸福度注入用的"人口加权平均附加税"：附加税按档是常数，权重取各档住户数。
            // 覆盖全部已识别密度的住户（含附加税为 0 的档），所以调高某一档会按人口占比稀释。
            long weightedSurcharge = 0;
            long weightedHouseholds = 0;
            for (int index = 0; index < householdsByCategory.Length; index++)
            {
                weightedSurcharge += (long)ZoneDensityCatalog.GetRateForCategory((DensityCategory)(index + 1)) * householdsByCategory[index];
                weightedHouseholds += householdsByCategory[index];
            }
            if (weightedHouseholds > 0)
            {
                DensityTaxHappinessSystem.AverageSurcharge = weightedSurcharge / (100f * weightedHouseholds);
                DensityTaxHappinessSystem.AverageSurchargeValid = true;
            }

            // 本遍扫描的应税基数与各档住户数落缓存：之后拖动税率、更新幸福度都不必再扫全城
            // （见 RecomputeFromCachedBasis）。
            for (int index = 0; index < basisTotals.Length; index++)
            {
                s_LastBasis[index] = basisTotals[index];
                s_LastCounts[index] = householdsByCategory[index];
            }
            s_BasisValid = true;

            for (int index = 0; index < categoryTotals.Length; index++)
                s_LastSettled[index] = (int)Unity.Mathematics.math.clamp(categoryTotals[index], int.MinValue, int.MaxValue);

            // 每次读档后的第一遍（空转预估）打一条诊断：户数漏斗 + 各档合计。
            if (!m_DiagLogged)
            {
                m_DiagLogged = true;
                Mod.log.Info($"密度税诊断：扫描{dScanned}户 | 跳过:无房产{dNoProperty}/无Prefab{dNoPrefab}/非住宅楼{dNoSpawnable}" +
                    $"/密度未识别{dUnknownZone}/附加税0={dZeroSurcharge}/免税线以上无余额={dBasisZero}/金额0={dAmountZero} | 计入{dSettled}户" +
                    $" | 各档合计 低{s_LastSettled[0]}/联排{s_LastSettled[1]}/中{s_LastSettled[2]}/高{s_LastSettled[3]}/廉租{s_LastSettled[4]}" +
                    $"（目录{m_Catalog.Count}个zone，经济参数实体{m_EconomyQuery.CalculateEntityCount()}个，免税线{minEarnings}，只免不补）");
            }

            // 空转预估到此为止：台账已填好，资金与统计一概不碰。
            if (estimateOnly)
            {
                return true;
            }

            if (total == 0)
            {
                return true;
            }

            // 城市金库收/支等额资金：钱在住户与城市之间转移，不凭空增减。
            int cityAmount = (int)Unity.Mathematics.math.clamp(total, int.MinValue, int.MaxValue);
            Entity city = m_CitySystem.City;
            if (city != Entity.Null && EntityManager.HasComponent<PlayerMoney>(city))
            {
                var money = EntityManager.GetComponentData<PlayerMoney>(city);
                money.Add(cityAmount);
                EntityManager.SetComponentData(city, money);
            }

            // 记进城市统计：正负分开记——税收记 住宅税收入，补贴记 原生"补助"支出行
            // （ExpenseSource.SubsidyResidential；原版负税率的补贴就是这么记的，且
            // BudgetApplySystem 的统计事件全部用正数幅值，符号不能塞进统计）。
            // 正负分开：否则高密度正税会被低密度补贴在账面上抵消，两行都看不清。
            // 原生面板住宅行的"预估"由 TaxEstimatePatch 加上 s_LastSettled 合计对齐口径；
            // 密度行的"预估"列直接显示本台账（读档后先由空转预估填充，见 DensityTaxPanelPatch）。
            // 用 vanilla 的作业模式入队：主线程直接写 NativeQueue 会和统计系统的消费作业
            // 竞争原生内存（release 版无安全检查，直接崩进程），必须排进依赖链。
            int taxPart = (int)Unity.Mathematics.math.clamp(totalPositive, int.MinValue, int.MaxValue);
            int subsidyPart = (int)Unity.Mathematics.math.clamp(-totalNegative, int.MinValue, int.MaxValue); // ≥ 0
            NativeQueue<StatisticsEvent> queue = m_CityStatisticsSystem.GetStatisticsEventQueue(out JobHandle deps);
            var job = new EnqueueStatisticsJob
            {
                m_Queue = queue,
                m_TaxEvent = taxPart > 0
                    ? new StatisticsEvent
                    {
                        m_Statistic = StatisticType.Income,
                        m_Parameter = (int)IncomeSource.TaxResidential,
                        m_Change = taxPart
                    }
                    : default,
                m_HasTaxEvent = taxPart > 0,
                m_SubsidyEvent = subsidyPart > 0
                    ? new StatisticsEvent
                    {
                        m_Statistic = StatisticType.Expense,
                        m_Parameter = (int)ExpenseSource.SubsidyResidential,
                        m_Change = subsidyPart
                    }
                    : default,
                m_HasSubsidyEvent = subsidyPart > 0,
            };
            JobHandle handle = IJobExtensions.Schedule(job, JobHandle.CombineDependencies(deps, base.Dependency));
            m_CityStatisticsSystem.AddWriter(handle);
            base.Dependency = JobHandle.CombineDependencies(base.Dependency, handle);
            return true;
        }

        /// <summary>统计事件入队作业：走 CityStatisticsSystem 的写者依赖链，避免主线程直写竞争。
        /// 税收（Income/TaxResidential）与补贴（Expense/SubsidyResidential）各一条，按需入队。</summary>
        private struct EnqueueStatisticsJob : IJob
        {
            public NativeQueue<StatisticsEvent> m_Queue;
            public StatisticsEvent m_TaxEvent;
            public bool m_HasTaxEvent;
            public StatisticsEvent m_SubsidyEvent;
            public bool m_HasSubsidyEvent;

            public void Execute()
            {
                if (m_HasTaxEvent)
                {
                    m_Queue.Enqueue(m_TaxEvent);
                }
                if (m_HasSubsidyEvent)
                {
                    m_Queue.Enqueue(m_SubsidyEvent);
                }
            }
        }

        // ---------- 存档序列化（IDefaultSerializable）：税率设置按存档隔离 ----------

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kSaveVersion);
            writer.Write(Settings.EnableDensityTax);
            writer.Write(Settings.DensityTax_Low);
            writer.Write(Settings.DensityTax_Row);
            writer.Write(Settings.DensityTax_Medium);
            writer.Write(Settings.DensityTax_High);
            writer.Write(Settings.DensityTax_LowRent);
            writer.Write(Settings.DensityDemandSensitivity);
            writer.Write(Settings.ResidentialMinimumEarnings);
            writer.Write(Settings.EnableWelfare);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out bool enableDensity);
            reader.Read(out int low);
            reader.Read(out int row);
            reader.Read(out int medium);
            reader.Read(out int high);
            reader.Read(out int lowRent);
            reader.Read(out float sensitivity);
            reader.Read(out int minEarnings);
            // v3 起追加福利开关；v2 存档没有该字段，保留 settings.txt 当前值。
            bool welfare = Settings.EnableWelfare;
            if (version >= 3)
            {
                reader.Read(out welfare);
            }

            Settings.EnableDensityTax = enableDensity;
            Settings.DensityTax_Low = low;
            Settings.DensityTax_Row = row;
            Settings.DensityTax_Medium = medium;
            Settings.DensityTax_High = high;
            Settings.DensityTax_LowRent = lowRent;
            Settings.DensityDemandSensitivity = sensitivity;
            Settings.ResidentialMinimumEarnings = minEarnings;
            Settings.EnableWelfare = welfare;
            Settings.RequestSave(); // 同步回 settings.txt，界面与磁盘一致。

            Mod.log.Info($"DensityTaxSystem: 存档税率已恢复（启用分密度附加税={(enableDensity ? "开" : "关")}，附加税 低{low}/联排{row}/中{medium}/高{high}/廉租{lowRent}，免税线{minEarnings}，福利{(welfare ? "开" : "关")}）。");
        }

        /// <summary>新城市（NewGame）不走 Deserialize：保留 settings.txt 的全局值作为起点。</summary>
        public void SetDefaults(Context context)
        {
            // 刻意为空：新城市沿用当前全局设置，不归零。
        }
    }
}
