using Colossal.Collections;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using HarmonyLib;
using TaxRateTweak.Services;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace TaxRateTweak.Systems
{
    public partial class FiveDensityDemandSystem : GameSystemBase
    {
        private static readonly AccessTools.FieldRef<ResidentialDemandSystem, NativeValue<int>> HouseholdRef =
            AccessTools.FieldRefAccess<ResidentialDemandSystem, NativeValue<int>>("m_HouseholdDemand");
        private static readonly AccessTools.FieldRef<ResidentialDemandSystem, bool> UnlimitedRef =
            AccessTools.FieldRefAccess<ResidentialDemandSystem, bool>("m_UnlimitedDemand");
        private EntityQuery m_Properties;
        private EntityQuery m_Zones;
        private EntityQuery m_Parameters;
        private ZoneDensityCatalog m_Catalog;
        private NativeParallelHashMap<Entity, int> m_Categories;
        private NativeArray<int> m_Counts;
        private JobHandle m_Readers;
        private readonly bool[] m_Unlocked = new bool[DensityDemand.Count];
        private readonly int[] m_Requirements = new int[DensityDemand.Count];
        private readonly int[] m_Taxes = new int[DensityDemand.Count];
        private int m_HouseholdDemand;
        private float m_Sensitivity;
        private bool m_Unlimited;
        private bool m_Logged;

        // 重算耗时统计：Recalculate 是每 16 模拟帧一次的全城住宅地产扫描 + 若干依赖同步点，
        // 是模组里唯一还没量化的一项开销，所以按分钟打一条平均/最大耗时。
        private long m_RecalcTotalMs;
        private int m_RecalcCalls;
        private int m_RecalcMaxMs;
        private int m_LastRecalcLogTick;

        /// <summary>由 ResidentialDemandPatch 在每次 Recalculate 调用后回报耗时；每分钟汇总一条日志。</summary>
        public void RecordCallTiming(int elapsedMs)
        {
            m_RecalcTotalMs += elapsedMs;
            m_RecalcCalls++;
            if (elapsedMs > m_RecalcMaxMs)
            {
                m_RecalcMaxMs = elapsedMs;
            }

            int now = System.Environment.TickCount;
            if (m_RecalcCalls > 0 && unchecked(now - m_LastRecalcLogTick) >= 60000)
            {
                m_LastRecalcLogTick = now;
                Mod.log.Info($"需求重算耗时：最近一分钟 {m_RecalcCalls} 次，平均 {m_RecalcTotalMs / m_RecalcCalls}ms，最大 {m_RecalcMaxMs}ms（每 16 模拟帧一次）。");
                m_RecalcTotalMs = 0;
                m_RecalcCalls = 0;
                m_RecalcMaxMs = 0;
            }
        }

        public bool Ready { get; private set; }
        public int Revision { get; private set; }
        public DensityDemand.Values Levels { get; private set; }
        public NativeParallelHashMap<Entity, int> Categories => m_Categories;
        public int[][] Factors { get; } = new int[DensityDemand.Count][];
        public int[] TaxEffects { get; } = new int[DensityDemand.Count];
        public int[] Total { get; } = new int[DensityDemand.Count];
        public int[] Free { get; } = new int[DensityDemand.Count];
        public bool IsUnlocked(int category) => m_Unlimited || m_Unlocked[category];

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Catalog = new ZoneDensityCatalog(World);
            m_Categories = new NativeParallelHashMap<Entity, int>(128, Allocator.Persistent);
            m_Counts = new NativeArray<int>(DensityDemand.Count * 2, Allocator.Persistent);
            for (int category = 0; category < DensityDemand.Count; category++) Factors[category] = new int[19];
            m_Zones = GetEntityQuery(ComponentType.ReadOnly<ZoneData>(), ComponentType.ReadOnly<ZonePropertiesData>());
            m_Parameters = GetEntityQuery(ComponentType.ReadOnly<DemandParameterData>());
            m_Properties = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ResidentialProperty>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Renter>() },
                None = new[] { ComponentType.ReadOnly<Condemned>(), ComponentType.ReadOnly<Destroyed>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
            });
        }

        protected override void OnDestroy()
        {
            m_Readers.Complete();
            Dependency.Complete();
            m_Counts.Dispose();
            m_Categories.Dispose();
            base.OnDestroy();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            Invalidate();
            m_Catalog.Invalidate();
            m_Logged = false;
            // 重挂就绪标志：运行期一次异常不该把五类需求永久关掉直到重启游戏。
            // 只在两个补丁确实挂上时重开——补丁没挂上时重试没有意义，只会每次读档刷一条 ERROR。
            if (Mod.FiveDensityPatchesApplied)
            {
                Mod.FiveDensityReady = true;
            }
        }

        protected override void OnUpdate() { }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            Invalidate();
            m_Catalog.Invalidate();
            m_Logged = false;
        }

        public void Invalidate()
        {
            m_Readers.Complete();
            Ready = false;
            Levels = default;
            Revision++;
        }

        public void AddReader(JobHandle reader)
        {
            m_Readers = JobHandle.CombineDependencies(m_Readers, reader);
        }

        public bool Recalculate(ResidentialDemandSystem vanilla)
        {
            if (m_Parameters.IsEmptyIgnoreFilter || m_Zones.IsEmptyIgnoreFilter) return false;
            var lowFactors = vanilla.GetLowDensityDemandFactors(out var lowDependency);
            var mediumFactors = vanilla.GetMediumDensityDemandFactors(out var mediumDependency);
            var highFactors = vanilla.GetHighDensityDemandFactors(out var highDependency);
            JobHandle.CombineDependencies(lowDependency, mediumDependency, highDependency).Complete();
            m_Readers.Complete();
            BuildCategories();
            m_Properties.CompleteDependency();
            EntityManager.CompleteDependencyBeforeRO<Household>();
            EntityManager.CompleteDependencyBeforeRO<SpawnableBuildingData>();
            EntityManager.CompleteDependencyBeforeRO<BuildingPropertyData>();
            EntityManager.CompleteDependencyBeforeRO<PropertyOnMarket>();
            EntityManager.CompleteDependencyBeforeRO<PropertyToBeOnMarket>();
            for (int index = 0; index < m_Counts.Length; index++) m_Counts[index] = 0;
            var countJob = new CountPropertiesJob
            {
                PrefabType = GetComponentTypeHandle<PrefabRef>(true),
                RenterType = GetBufferTypeHandle<Renter>(true),
                OnMarketType = GetComponentTypeHandle<PropertyOnMarket>(true),
                ToMarketType = GetComponentTypeHandle<PropertyToBeOnMarket>(true),
                Spawnable = GetComponentLookup<SpawnableBuildingData>(true),
                Properties = GetComponentLookup<BuildingPropertyData>(true),
                Households = GetComponentLookup<Household>(true),
                Categories = m_Categories,
                Counts = m_Counts
            };
            Dependency = countJob.Schedule(m_Properties, Dependency);
            Dependency.Complete();
            var parameters = m_Parameters.GetSingleton<DemandParameterData>();
            m_HouseholdDemand = HouseholdRef(vanilla).value;
            m_Unlimited = UnlimitedRef(vanilla);
            for (int category = 0; category < DensityDemand.Count; category++)
            {
                var native = category == 0 ? lowFactors : category < 3 ? mediumFactors : highFactors;
                for (int factor = 0; factor < Factors[category].Length; factor++) Factors[category][factor] = native[factor];
                Total[category] = m_Counts[category];
                Free[category] = m_Counts[DensityDemand.Count + category];
                m_Requirements[category] = category == 0 ? parameters.m_FreeResidentialRequirement.x :
                    category < 3 ? parameters.m_FreeResidentialRequirement.y : parameters.m_FreeResidentialRequirement.z;
            }
            Ready = true;
            ComputeDemand();
            if (!m_Logged)
            {
                m_Logged = true;
                Mod.log.Info($"Five-density demand 1.8.26 ready: low/row/medium/high/low-rent; properties {string.Join("/", Total)}; unlocked {string.Join("/", m_Unlocked)}; demand {Levels.Low}/{Levels.Row}/{Levels.Medium}/{Levels.High}/{Levels.LowRent}; no spawn culling.");
            }
            return true;
        }

        public void RefreshTaxes()
        {
            if (!Ready) return;
            for (int category = 0; category < DensityDemand.Count; category++)
            {
                if (m_Taxes[category] != TaxRate(category) || m_Sensitivity != Settings.DensityDemandSensitivity)
                {
                    ComputeDemand();
                    return;
                }
            }
        }

        private void ComputeDemand()
        {
            var values = new int[DensityDemand.Count];
            m_Sensitivity = Settings.DensityDemandSensitivity;
            for (int category = 0; category < DensityDemand.Count; category++)
            {
                int vacancy = DensityDemand.Vacancy(Free[category], m_Requirements[category]);
                int[] factors = Factors[category];
                int totalFactors = factors[7] + factors[11] + factors[6] + factors[5];
                if (category > 0) totalFactors += factors[12];
                if (category >= 3) totalFactors += factors[8];
                m_Taxes[category] = TaxRate(category);
                TaxEffects[category] = (int)math.round(-m_Taxes[category] * m_Sensitivity);
                values[category] = DensityDemand.Calculate(m_HouseholdDemand, vacancy, totalFactors,
                    TaxEffects[category], m_Unlocked[category], m_Unlimited);
                factors[13] = Total[category] > 0 ? vacancy : 0;
                factors[18] = Total[category] == 0 ? vacancy : 0;
            }
            Levels = new DensityDemand.Values { Low = values[0], Row = values[1], Medium = values[2], High = values[3], LowRent = values[4] };
            Revision++;
        }

        private static int TaxRate(int category)
        {
            return ZoneDensityCatalog.GetRateForCategory((DensityCategory)(category + 1));
        }

        private void BuildCategories()
        {
            m_Catalog.EnsureBuilt();
            m_Categories.Clear();
            using (var entities = m_Zones.ToEntityArray(Allocator.Temp))
            {
                if (m_Categories.Capacity < entities.Length) m_Categories.Capacity = entities.Length;
                for (int category = 0; category < DensityDemand.Count; category++) m_Unlocked[category] = false;
                foreach (var entity in entities)
                {
                    var zone = EntityManager.GetComponentData<ZoneData>(entity);
                    if (zone.m_AreaType != AreaType.Residential) continue;
                    int category = (int)m_Catalog.GetCategory(entity) - 1;
                    if (category < 0)
                    {
                        var density = PropertyUtils.GetZoneDensity(zone, EntityManager.GetComponentData<ZonePropertiesData>(entity));
                        category = density == ZoneDensity.Low ? 0 : density == ZoneDensity.Medium ? 2 : 3;
                    }
                    m_Categories.TryAdd(entity, category);
                    if (!EntityManager.HasComponent<Locked>(entity) || !EntityManager.IsComponentEnabled<Locked>(entity))
                        m_Unlocked[category] = true;
                }
            }
        }

        [BurstCompile]
        private struct CountPropertiesJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<PrefabRef> PrefabType;
            [ReadOnly] public BufferTypeHandle<Renter> RenterType;
            [ReadOnly] public ComponentTypeHandle<PropertyOnMarket> OnMarketType;
            [ReadOnly] public ComponentTypeHandle<PropertyToBeOnMarket> ToMarketType;
            [ReadOnly] public ComponentLookup<SpawnableBuildingData> Spawnable;
            [ReadOnly] public ComponentLookup<BuildingPropertyData> Properties;
            [ReadOnly] public ComponentLookup<Household> Households;
            [ReadOnly] public NativeParallelHashMap<Entity, int> Categories;
            public NativeArray<int> Counts;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var prefabs = chunk.GetNativeArray(ref PrefabType);
                var renters = chunk.GetBufferAccessor(ref RenterType);
                bool market = chunk.Has(ref OnMarketType) || chunk.Has(ref ToMarketType);
                for (int building = 0; building < prefabs.Length; building++)
                {
                    Entity prefab = prefabs[building].m_Prefab;
                    if (!Spawnable.TryGetComponent(prefab, out var spawnable) ||
                        !Properties.TryGetComponent(prefab, out var properties) ||
                        !Categories.TryGetValue(spawnable.m_ZonePrefab, out int category)) continue;
                    int capacity = PropertyUtils.GetResidentialProperties(properties);
                    Counts[category] += capacity;
                    if (!market) continue;
                    int occupied = 0;
                    var buffer = renters[building];
                    for (int renter = 0; renter < buffer.Length; renter++)
                        if (Households.HasComponent(buffer[renter].m_Renter)) occupied++;
                    Counts[DensityDemand.Count + category] += math.max(0, capacity - occupied);
                }
            }
        }
    }
}
