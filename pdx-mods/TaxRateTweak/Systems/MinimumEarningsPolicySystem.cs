using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.Policies;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using UnityEngine;

namespace TaxRateTweak.Systems
{
    /// <summary>
    /// 住宅免税线政策：运行时创建一个真实的政策 prefab（PolicySliderPrefab），
    /// 让它出现在原生 城市信息→城市政策 面板里（勾选启用 + 滑块调金额，状态随存档保存）。
    /// 每帧把城市实体 Policy 缓冲区里该政策的 激活状态/调整值 同步进
    /// Settings.ResidentialMinimumEarnings（-1 = 关闭），由 DensityTaxSystem 推进经济参数。
    /// 政策值只在变化时才覆盖 Settings，玩家从未动过政策时保留 settings.txt 里的手动值。
    /// </summary>
    public partial class MinimumEarningsPolicySystem : GameSystemBase
    {
        // 政策 prefab 名：本地化键 Policy.TITLE/DESCRIPTION[该名]、存档 PrefabID 重映射都用它，勿改。
        public const string kPolicyName = "Residential Minimum Earnings";

        private PrefabSystem m_PrefabSystem;
        private CitySystem m_CitySystem;
        private Entity m_PolicyEntity;
        private float m_LastSynced = float.NaN;
        // 上一次看到的滑块原始值。必须与 m_LastSynced 分开：未勾选时 m_LastSynced 恒为 -1，
        // 只看它无法察觉"玩家在未勾选状态下拖了滑块"——而那正是最容易被当成"免税线没用"的情形。
        private float m_LastSeenAdjustment = float.NaN;
        // "未勾选但拖了滑块"提示的限流刻度（拖动会连发大量值变化）。
        private int m_SliderIgnoredLogTick;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            // 读档后政策实体句柄被重映射，同步状态必须一起重置：否则新存档的政策值与
            // 上一个存档留下的 m_LastSynced 恰好相同时会被跳过，Settings 会残留旧存档的免税线。
            m_PolicyEntity = Entity.Null;
            m_LastSynced = float.NaN;
            m_LastSeenAdjustment = float.NaN;
        }

        protected override void OnUpdate()
        {
            try
            {
                if (m_PolicyEntity == Entity.Null || !EntityManager.HasComponent<PolicyData>(m_PolicyEntity))
                {
                    m_PolicyEntity = EnsureCreated(m_PrefabSystem, EntityManager);
                }
                else if (EntityManager.HasComponent<Locked>(m_PolicyEntity) &&
                         EntityManager.IsComponentEnabled<Locked>(m_PolicyEntity))
                {
                    // 防御：万一其它系统把政策重新锁上，立刻解锁（锁定会禁用滑块交互）。
                    EntityManager.SetComponentEnabled<Locked>(m_PolicyEntity, false);
                }
                SyncFromPolicy();
            }
            catch (System.Exception ex)
            {
                // 绝不崩游戏；仅在调试模式下记录。
                if (Settings.DebugLogging)
                {
                    Mod.log.Warn($"MinimumEarningsPolicySystem: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 确保政策 prefab 已注册并返回其实体（幂等）。
        /// 静态方法：Mod.OnLoad 在主菜单阶段就调用它——存档加载时的 PrefabID 重映射
        /// 要求政策实体在读档前已存在，不能等到游戏内的模拟阶段再创建。
        /// </summary>
        public static Entity EnsureCreated(PrefabSystem prefabSystem, EntityManager entityManager)
        {
            // 模组重载/热重编译时 prefab 可能已注册过，直接解析实体。
            var id = new PrefabID(nameof(PolicySliderPrefab), kPolicyName);
            if (prefabSystem.TryGetPrefab(id, out PrefabBase existingBase) &&
                existingBase is PolicySliderPrefab existing &&
                prefabSystem.TryGetEntity(existing, out Entity existingEntity))
            {
                EnsureCityOptionComponent(entityManager, existingEntity);
                return existingEntity;
            }

            var prefab = ScriptableObject.CreateInstance<PolicySliderPrefab>();
            prefab.name = kPolicyName;
            prefab.m_Visibility = PolicyVisibility.Default; // 显示在政策列表里
            prefab.m_Category = PolicyCategory.Budget;
            // 原生前端把滑块值当真值判断（city-policy.tsx: e.data && r && <Slider/>），
            // 值恰好为 0 时整个滑块组件不渲染、玩家永远失去调整入口。
            // 对策：下限用 0.01 而非 0——money 格式 toFixed(0) 显示为 ¢0，
            // 同步层 RoundToInt 后也归 0，显示与实效都是 0，但 JS 真值永远成立。
            prefab.m_SliderRange = new Bounds1(0.01f, 50000f);
            prefab.m_SliderDefault = 2000f;
            prefab.m_SliderStep = 100f;
            prefab.m_Unit = PolicySliderUnit.money;

            var ui = ScriptableObject.CreateInstance<UIObject>();
            ui.name = kPolicyName + " UI";
            ui.m_Icon = "Media/Game/Policies/BigBusinessBenefactor.svg";
            ui.m_Priority = 100; // 排在原生政策之后
            prefab.components.Add(ui);

            if (prefabSystem.AddPrefab(prefab) && prefabSystem.TryGetEntity(prefab, out Entity entity))
            {
                EnsureCityOptionComponent(entityManager, entity);
                Mod.log.Info("TaxRateTweak: 免税线政策已加入城市政策面板。");
                return entity;
            }
            return Entity.Null;
        }

        /// <summary>
        /// 城市政策列表的查询要求 PolicyData + (CityOptionData | CityModifierData)。
        /// 空掩码的 CityOptionData 只让政策出现在列表里，不对城市产生任何原生效果。
        /// 另外：PrefabSystem 会给可解锁的 prefab 加 Locked 组件（默认锁定），
        /// 锁定的政策在前端只显示里程碑数字、不给滑块也不让勾选——必须显式解锁。
        /// </summary>
        private static void EnsureCityOptionComponent(EntityManager entityManager, Entity entity)
        {
            if (entity == Entity.Null)
            {
                return;
            }
            if (!entityManager.HasComponent<CityOptionData>(entity))
            {
                entityManager.AddComponentData(entity, new CityOptionData { m_OptionMask = 0u });
            }
            if (entityManager.HasComponent<Locked>(entity))
            {
                entityManager.SetComponentEnabled<Locked>(entity, false);
            }
        }

        private void SyncFromPolicy()
        {
            if (m_PolicyEntity == Entity.Null)
            {
                return;
            }
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.TryGetBuffer(city, true, out DynamicBuffer<Policy> policies))
            {
                return;
            }

            for (int i = 0; i < policies.Length; i++)
            {
                if (policies[i].m_Policy != m_PolicyEntity)
                {
                    continue;
                }
                bool active = (policies[i].m_Flags & PolicyFlags.Active) != 0;
                float adjustment = policies[i].m_Adjustment;

                // 未勾选时滑块值不生效（阈值按 0 处理）。原生前端未勾选也照样渲染滑块，
                // 拖动会连发大量值变化，所以按 3 秒限流记一条，便于事后对照。
                if (adjustment != m_LastSeenAdjustment)
                {
                    m_LastSeenAdjustment = adjustment;
                    if (!active && adjustment != 0f)
                    {
                        int now = System.Environment.TickCount;
                        if (unchecked(now - m_SliderIgnoredLogTick) >= 3000)
                        {
                            m_SliderIgnoredLogTick = now;
                            Mod.log.Info($"免税线政策未勾选：阈值按 0 处理（滑块值 {Mathf.RoundToInt(adjustment)} 暂不生效）；勾选后才使用滑块值。");
                        }
                    }
                }

                if (active && adjustment == 0f)
                {
                    // 自愈：旧版本允许存出恰好为 0 的调整值，而前端对 0 值不渲染滑块，
                    // 玩家会永远失去调整入口。写成 0.01（显示 ¢0，实效 0）保住滑块。
                    var writable = EntityManager.GetBuffer<Policy>(city);
                    Policy entry = writable[i];
                    entry.m_Adjustment = 0.01f;
                    writable[i] = entry;
                    EntityManager.AddComponent<Game.Common.Updated>(city); // 触发政策列表绑定刷新
                    adjustment = 0.01f;
                }

                // 政策语义（按需求定义）：勾选 = 用玩家选的滑块值；未勾选 = 不设免征线 = 阈值 0。
                // 未勾选不再是"恢复原版值"——那样滑块在未勾选时完全无效，玩家看到的就是
                // "免税线什么用都没有"。注意阈值 0 等于取消原版那条平衡用免征额，全部工资
                // 计入应税收入，这与把滑块拉到最左端的效果一致。
                float value = active ? adjustment : 0f;
                if (value != m_LastSynced)
                {
                    m_LastSynced = value;
                    int target = Mathf.Max(0, Mathf.RoundToInt(value));
                    if (Settings.ResidentialMinimumEarnings != target)
                    {
                        Settings.ResidentialMinimumEarnings = target;
                        Settings.RequestSave(); // 与 settings.txt 保持一致
                        // 无条件记录：这是"免税线到底有没有生效"三段链路的头一段
                        // （政策状态 → 设置 → 经济参数），后两段在 DensityTaxSystem。
                        Mod.log.Info(active
                            ? $"免税线政策已勾选：阈值 {target}。"
                            : "免税线政策未勾选：阈值 0（全部工资计入应税收入）。");
                    }
                }
                return;
            }
            // 无条目 = 玩家从未动过该政策 → 不碰 Settings（保留 settings.txt 里的手动值）。
        }
    }
}
