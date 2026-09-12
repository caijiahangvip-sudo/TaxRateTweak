namespace TaxRateTweak.Services
{
    public sealed class EstimateRefreshState
    {
        private readonly int[] m_Rates = new int[5];
        private bool m_Valid;
        private int m_Threshold;
        private bool m_Welfare;
        private uint m_Frame;
        private int m_Tick;

        public void Reset() => m_Valid = false;

        public bool IsDue(uint frame, int tick)
        {
            if (!m_Valid) return true;
            uint elapsed = unchecked((uint)(tick - m_Tick));
            bool changed = m_Threshold != Settings.ResidentialMinimumEarnings || m_Welfare != Settings.EnableWelfare;
            for (int index = 0; index < m_Rates.Length; index++)
                changed |= m_Rates[index] != Settings.GetDensityRate(index);
            // 免税线/福利变化会改变税基，必须重扫；节流到 1 秒一次，避免拖免税线滑块时反复付
            // 一遍全城扫描（实测一遍 47ms）。
            if (changed) return elapsed >= 1000u;
            // 稳态：只做很久没刷新时的兜底。税率变化走"缓存基数重算"（不经过这里），税基只在游戏
            // 推进时才漂移，而每游戏日结算本来就会完整重扫一遍——所以周期性扫描纯属浪费，
            // 实测那一遍要 47ms，每 30 秒一卡。
            return elapsed >= 300000u || (elapsed >= 16u && unchecked(frame - m_Frame) >= 65536u);
        }

        public void Record(uint frame, int tick)
        {
            for (int index = 0; index < m_Rates.Length; index++) m_Rates[index] = Settings.GetDensityRate(index);
            m_Threshold = Settings.ResidentialMinimumEarnings;
            m_Welfare = Settings.EnableWelfare;
            m_Frame = frame;
            m_Tick = tick;
            m_Valid = true;
        }
    }
}
