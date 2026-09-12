using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;

namespace TaxRateTweak.Services
{
    /// <summary>住宅密度档位。密度不存在于任何组件字段，只能从 ZonePrefab 名字推断。</summary>
    public enum DensityCategory
    {
        Unknown,
        Low,
        Row,
        Medium,
        High,
        LowRent
    }

    /// <summary>
    /// 住宅密度目录：CS2 的住宅密度编码在 ZonePrefab 的名字里
    /// （"Residential Low" / "Residential Row" / "Residential Medium" /
    /// "Residential High" / "Residential Low Rent"）。这里把所有住宅 zone prefab 实体
    /// 按名字分类并缓存，供 DensityTaxSystem 做 O(1) 查询。
    /// </summary>
    public sealed class ZoneDensityCatalog
    {
        private readonly Dictionary<Entity, DensityCategory> _byZonePrefab = new Dictionary<Entity, DensityCategory>();
        private World _world;
        // 建目录时的全库 prefab 实体总数：中途 DLC/资产 mod 加入新 zone 时触发重建。
        private int _lastPrefabCount = -1;
        // 上次做"全库 prefab 计数"的墙钟刻度。计数要 CreateEntityQuery，不能每次调用都做。
        private int _lastCountTick;
        // 计数检查的最小间隔。资产流式加载会让全库 prefab 总数常态抖动，逐次比对会导致
        // 目录反复重建并刷日志（实测约每 26 秒一次）。5 秒一次足够覆盖新 zone 的加入。
        private const int kCountCheckIntervalMs = 5000;

        /// <summary>
        /// 目录为空时立即重建；否则至多每 kCountCheckIntervalMs 检查一次全库 prefab 总数，
        /// 且只在数量"增加"（新资产/新 zone 加入）时重建。数量减少不重建：被删除的 zone prefab
        /// 只会留下过期键，Entity 带版本号，句柄被回收后不会命中，最多退化为 Unknown。
        /// </summary>
        public void EnsureBuilt()
        {
            if (_byZonePrefab.Count == 0)
            {
                Rebuild();
                return;
            }

            int now = System.Environment.TickCount;
            if (unchecked(now - _lastCountTick) < kCountCheckIntervalMs)
            {
                return;
            }
            _lastCountTick = now;

            int current = CountWorldPrefabs();
            if (_lastPrefabCount >= 0 && current > _lastPrefabCount)
            {
                Rebuild();
            }
        }

        /// <summary>全库 prefab 实体总数（PrefabData 查询，计数廉价）。</summary>
        private int CountWorldPrefabs()
        {
            try
            {
                using (var q = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Prefabs.PrefabData>()))
                {
                    return q.CalculateEntityCount();
                }
            }
            catch (Exception)
            {
                return _lastPrefabCount < 0 ? 0 : _lastPrefabCount;
            }
        }

        public ZoneDensityCatalog(World world)
        {
            _world = world;
        }

        /// <summary>目录条目数（仅住宅 zone）。</summary>
        public int Count => _byZonePrefab.Count;

        /// <summary>
        /// 读档/世界重建后调用：zone prefab 实体句柄被重映射，旧缓存全部失效，
        /// 必须清空待下一次访问时重建。
        /// </summary>
        public void Invalidate()
        {
            _byZonePrefab.Clear();
            _lastPrefabCount = -1;
        }

        /// <summary>
        /// 取某 zone prefab 实体的密度档位；未命中返回 Unknown。
        /// 纯字典查询，不得在热路径里调 EnsureBuilt（它每次要 CreateEntityQuery+全库计数，
        /// 逐户调用会把一遍结算拖到 600ms）。调用方必须在遍历前自己 EnsureBuilt 一次。
        /// </summary>
        public DensityCategory GetCategory(Entity zonePrefabEntity)
        {
            if (_byZonePrefab.Count == 0)
            {
                EnsureBuilt(); // 仅防空目录兜底；稳态下是零开销的 Count 判断。
            }

            DensityCategory category;
            if (_byZonePrefab.TryGetValue(zonePrefabEntity, out category))
            {
                return category;
            }
            return DensityCategory.Unknown;
        }

        /// <summary>取某密度档位当前配置的附加税率（%，读 Settings；Unknown 恒为 0）。</summary>
        public static int GetRateForCategory(DensityCategory category)
        {
            switch (category)
            {
                case DensityCategory.Low: return Settings.DensityTax_Low;
                case DensityCategory.Row: return Settings.DensityTax_Row;
                case DensityCategory.Medium: return Settings.DensityTax_Medium;
                case DensityCategory.High: return Settings.DensityTax_High;
                case DensityCategory.LowRent: return Settings.DensityTax_LowRent;
                default: return 0;
            }
        }

        /// <summary>
        /// 扫描所有带 ZoneData 的实体（即 zone prefab 实体），过滤住宅，
        /// 用 PrefabSystem.GetPrefabName 取名后按名字分类。
        /// </summary>
        private void Rebuild()
        {
            _byZonePrefab.Clear();
            _lastPrefabCount = CountWorldPrefabs();
            _lastCountTick = System.Environment.TickCount;

            try
            {
                var prefabSystem = _world.GetExistingSystemManaged<PrefabSystem>();
                if (prefabSystem == null)
                {
                    return;
                }

                var em = _world.EntityManager;
                var query = em.CreateEntityQuery(ComponentType.ReadOnly<ZoneData>());

                using (var entities = query.ToEntityArray(Allocator.Temp))
                using (var zones = query.ToComponentDataArray<ZoneData>(Allocator.Temp))
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        if (zones[i].m_AreaType != AreaType.Residential)
                        {
                            continue;
                        }

                        string name = null;
                        try
                        {
                            name = prefabSystem.GetPrefabName(entities[i]);
                        }
                        catch (Exception)
                        {
                            // 单个 prefab 取名失败不影响其余。
                        }

                        _byZonePrefab[entities[i]] = Classify(name);
                    }
                }

                // 普查日志与"引擎需求槽核对"只在调试模式下产出：无条件 INFO 时会在资产流式
                // 加载较多的环境里每次重建都刷两行（实测每分钟级别），把真正有用的日志挤掉。
                if (Settings.DebugLogging)
                {
                    var census = new int[6]; // 索引对齐 DensityCategory：Unknown/Low/Row/Medium/High/LowRent
                    // 引擎需求槽核对：每个模组档位里的 zone 在引擎 GetZoneDensity 下分别落入哪个
                    // 原生需求槽（int3 低/中/高）。刷楼 job 只认这三个槽，联排/廉租在引擎里的
                    // 实际归属决定我们需求联动该怎么合并才对。
                    var bucketCount = new int[6, 3]; // [category, ZoneDensity(Low/Medium/High)]
                    foreach (var kv in _byZonePrefab)
                    {
                        census[(int)kv.Value]++;
                        try
                        {
                            if (em.HasComponent<ZonePropertiesData>(kv.Key) && em.HasComponent<ZoneData>(kv.Key))
                            {
                                var zd = Game.Buildings.PropertyUtils.GetZoneDensity(
                                    em.GetComponentData<ZoneData>(kv.Key),
                                    em.GetComponentData<ZonePropertiesData>(kv.Key));
                                bucketCount[(int)kv.Value, (int)zd]++;
                            }
                        }
                        catch (Exception)
                        {
                            // 单个 zone 核对失败不影响其余。
                        }
                    }
                    string Buckets(int cat) => $"低{bucketCount[cat, 0]}/中{bucketCount[cat, 1]}/高{bucketCount[cat, 2]}";
                    Mod.log.Info($"密度目录已建：住宅zone共 {_byZonePrefab.Count} 个（低{census[1]}/联排{census[2]}/中{census[3]}/高{census[4]}/廉租{census[5]}，未识别{census[0]}）。");
                    Mod.log.Info($"引擎需求槽核对：低档[{Buckets(1)}] 联排[{Buckets(2)}] 中档[{Buckets(3)}] 高档[{Buckets(4)}] 廉租[{Buckets(5)}] 未识别[{Buckets(0)}]");
                }
            }
            catch (Exception ex)
            {
                if (Settings.DebugLogging)
                {
                    Mod.log.Warn($"ZoneDensityCatalog rebuild failed: {ex.Message}");
                }
            }
        }

        /// <summary>按 zone 名字（忽略大小写）分类；顺序敏感，先匹配更具体的名字。</summary>
        private static DensityCategory Classify(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return DensityCategory.Unknown;
            }

            string n = name.ToLowerInvariant();

            if (n.Contains("lowrent") || n.Contains("low rent"))
            {
                return DensityCategory.LowRent;
            }
            if (n.Contains("row"))
            {
                return DensityCategory.Row;
            }
            if (n.Contains("high"))
            {
                return DensityCategory.High;
            }
            if (n.Contains("medium"))
            {
                return DensityCategory.Medium;
            }
            if (n.Contains("low"))
            {
                return DensityCategory.Low;
            }
            return DensityCategory.Unknown;
        }
    }
}
