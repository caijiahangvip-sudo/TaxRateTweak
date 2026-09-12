using Game;
using Game.SceneFlow;
using Game.Simulation;
using Unity.Entities;
using TaxRateTweak.Services;

namespace TaxRateTweak.Systems
{
    /// <summary>
    /// 密度税预估刷新器。挂在 UIUpdate 阶段（与 TaxationUISystem 同阶段）：
    /// 游戏暂停时 GameSimulation 整体停摆，但税收面板的"预估"列必须像原生学历行一样
    /// 暂停时也照常显示/刷新，所以预估从 DensityTaxSystem 拆出来放到这里。
    /// 只做空转预估（不动钱、不动统计）。刷新节奏：
    /// 设置变化时下一次 UI 帧即可刷新（16ms 最小间隔）；其余每 1024 模拟帧或 1 秒刷新。
    /// </summary>
    public partial class DensityEstimateSystem : GameSystemBase
    {
        private DensityTaxSystem m_DensityTaxSystem;
        private SimulationSystem m_SimulationSystem;
        private readonly EstimateRefreshState m_Refresh = new EstimateRefreshState();
        private bool m_AliveLogged;
        private int m_LastErrorTick;
        private int m_TimingLogsLeft = 3;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_DensityTaxSystem = World.GetOrCreateSystemManaged<DensityTaxSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
        }

        /// <summary>
        /// 让下一次 UIUpdate 立刻重算预估（税率、免税线、福利改变时由面板补丁调用）。
        /// 不在这里直接跑扫描：一次全城扫描约 15ms（实测 5.4 万户），拖动滑块会连发事件，
        /// 主线程连续 15ms 停顿就是不跟手/掉帧的来源。合并到下一个 UI 帧只有约一帧延迟，
        /// 与原生"改动后由绑定更新带走"的节奏一致。
        /// </summary>
        public void InvalidateRefresh()
        {
            m_Refresh.Reset();
        }

        protected override void OnUpdate()
        {
            try
            {
                if (!Settings.Enabled || !Settings.EnableDensityTax)
                {
                    m_Refresh.Reset();
                    return;
                }
                if (!GameManager.instance.gameMode.IsGame())
                {
                    return;
                }
                if (!m_AliveLogged)
                {
                    m_AliveLogged = true;
                    Mod.log.Info("密度预估刷新器已启动（UIUpdate 阶段）。");
                }

                uint frame = m_SimulationSystem.frameIndex;
                int tick = System.Environment.TickCount;
                if (!m_Refresh.IsDue(frame, tick)) return;
                // 世界没就绪（目录/经济参数未建）时 RunEstimate 返回 false，下帧再试。
                if (m_DensityTaxSystem.RunEstimate())
                {
                    m_Refresh.Record(frame, tick);
                    Patches.DensityTaxPanelPatch.RefreshEstimateBindings(
                        World.GetOrCreateSystemManaged<Game.UI.InGame.TaxationUISystem>());
                    if (m_TimingLogsLeft > 0)
                    {
                        m_TimingLogsLeft--;
                        Mod.log.Info($"密度预估单遍耗时 {System.Environment.TickCount - tick}ms。");
                    }
                }
            }
            catch (System.Exception ex)
            {
                // 预估通路绝不允许静默失败；每秒最多一条，避免刷屏。
                int now = System.Environment.TickCount;
                if (now - m_LastErrorTick >= 1000)
                {
                    m_LastErrorTick = now;
                    Mod.log.Warn($"DensityEstimateSystem: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_Refresh.Reset();
            m_AliveLogged = false;
            m_TimingLogsLeft = 3;
        }
    }
}
