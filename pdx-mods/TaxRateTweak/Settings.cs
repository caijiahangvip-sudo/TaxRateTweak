using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Game.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TaxRateTweak
{
    public static class Settings
    {
        // 所有 public static 字段通过反射整体持久化到 ModsData/TaxRateTweak/settings.txt
        // （key=value 行格式，无第三方 JSON 依赖），重启游戏后自动恢复。

        public static bool Enabled = true;
        public static float TaxRateMin = -10.0f;   // 全局税率下限（比例，-10.0 = -1000%，负税率=政府补贴）
        public static float TaxRateMax = 1.0f;     // 全局税率上限（1.0 = +100%，正税率封顶，超过100%无意义）
        public static bool EnableDistrictTaxes = true; // 启用分区独立税率
        public static bool EnableZoneTaxes = true; // 启用按区域类型的扩展税率
        public static float DefaultDistrictTaxRate = 0.10f; // 新行政区的默认税率（0.10 = 10%）
        public static bool ClampToSafeLimits = true; // 启用安全硬钳制
        public static float SafeMin = -10.0f;      // 安全下限（-1000%）
        public static float SafeMax = 1.0f;        // 安全上限（+100%）
        public static float SliderStep = 0.01f;    // 滑块步进精度（0.01 = 每次 1%）
        public static bool DebugLogging = false;   // 输出调试日志
        // 分区（区域类型）税率覆盖：0 = 不覆盖（使用游戏自身税率）。
        public static int ZoneTaxRate_Residential = 0; // 住宅税率覆盖（百分比，0 = 不覆盖）
        public static int ZoneTaxRate_Commercial = 0;  // 商业税率覆盖（百分比，0 = 不覆盖）
        public static int ZoneTaxRate_Industrial = 0;  // 工业税率覆盖（百分比，0 = 不覆盖）
        public static int ZoneTaxRate_Office = 0;      // 办公税率覆盖（百分比，0 = 不覆盖）

        // 分密度住宅附加税：按住宅建筑的密度档位对住户存款做真实增减（负=补贴，0=不调整）。
        // 结算节奏：每游戏日一次，金额 = (昨日工资 - 免税线) × 税率% 。
        public static bool EnableDensityTax = true;        // 启用分密度住宅附加税
        public static int DensityTax_Low = 0;              // 低密度住宅附加税率 %（可负=补贴，0=不调整）
        public static int DensityTax_Row = 0;              // 联排住宅
        public static int DensityTax_Medium = 0;           // 中密度住宅
        public static int DensityTax_High = 0;             // 高密度住宅
        public static int DensityTax_LowRent = 0;          // 廉租房
        public static float DensityDemandSensitivity = 1.0f; // 需求敏感度：每 ±1% 附加税率 → 需求移动的点数

        // 住宅免税线（EconomyParameterData.m_ResidentialMinimumEarnings）覆盖：
        // -1 = 不修改（用游戏默认）；0 及以上 = 每帧强制覆盖为该值（最低 0，不设上限）。
        public static int ResidentialMinimumEarnings = -1;

        // 【已废弃 · 1.8.14 起不再使用】原"负所得税"开关：正附加税时给免税线以下的家庭
        // 按差额发补贴。该行为已删除——免税线的语义是"只免不补"：低于线的家庭应缴 0，
        // 不发钱。（否则免税线设到 50000 会变成按"免税线 × 附加率"给全城发钱，瞬间掏空金库。）
        // 字段保留只为不改动存档格式：DensityTaxSystem 的序列化仍按原顺序读写它。
        public static bool EnableWelfare = true;

        public static void EnsureDefaults()
        {
            // 默认值已在字段里设置；此处仅为扩展预留。
        }

        // ---------- 分密度附加税：按面板行索引读写（0=低 1=联排 2=中 3=高 4=廉租） ----------

        public static int GetDensityRate(int index)
        {
            switch (index)
            {
                case 0: return DensityTax_Low;
                case 1: return DensityTax_Row;
                case 2: return DensityTax_Medium;
                case 3: return DensityTax_High;
                case 4: return DensityTax_LowRent;
                default: return 0;
            }
        }

        public static void SetDensityRate(int index, int rate)
        {
            switch (index)
            {
                case 0: DensityTax_Low = rate; break;
                case 1: DensityTax_Row = rate; break;
                case 2: DensityTax_Medium = rate; break;
                case 3: DensityTax_High = rate; break;
                case 4: DensityTax_LowRent = rate; break;
            }
        }

        // ---------- 持久化（防抖：标脏后由 DensityTaxSystem 每帧检查落盘） ----------

        private static bool _dirty;
        private static float _lastSaveTime = -999f;
        private const float SaveDebounceSeconds = 3f;

        private static string SettingsFilePath =>
            Path.Combine(Application.persistentDataPath, "ModsData", "TaxRateTweak", "settings.txt");

        /// <summary>标脏并请求保存；实际写盘由 FlushSaveIfNeeded 防抖执行。</summary>
        public static void RequestSave()
        {
            _dirty = true;
        }

        /// <summary>每帧调用（DensityTaxSystem.OnUpdate）；脏且超过防抖间隔则落盘。</summary>
        public static void FlushSaveIfNeeded()
        {
            if (!_dirty)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now - _lastSaveTime < SaveDebounceSeconds)
            {
                return;
            }
            _dirty = false;
            _lastSaveTime = now;
            Save();
        }

        /// <summary>立即把所有 public static 字段写入 settings.txt。</summary>
        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (FieldInfo f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    object v = f.GetValue(null);
                    if (v is bool || v is int || v is float)
                    {
                        sb.Append(f.Name).Append('=')
                          .Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n');
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath));
                File.WriteAllText(SettingsFilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"TaxRateTweak: settings save failed: {ex.Message}");
            }
        }

        /// <summary>启动时读回 settings.txt；未知键/坏值静默跳过。</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    return;
                }
                var fields = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Static);
                foreach (string line in File.ReadAllLines(SettingsFilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string name = line.Substring(0, eq);
                    string raw = line.Substring(eq + 1);
                    foreach (FieldInfo f in fields)
                    {
                        if (f.Name != name)
                        {
                            continue;
                        }
                        if (f.FieldType == typeof(bool) && bool.TryParse(raw, out bool b)) { f.SetValue(null, b); }
                        else if (f.FieldType == typeof(int) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) { f.SetValue(null, i); }
                        else if (f.FieldType == typeof(float) && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float fl)) { f.SetValue(null, fl); }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"TaxRateTweak: settings load failed: {ex.Message}");
            }
        }

        public static int MinTaxRatePercent => Mathf.RoundToInt(TaxRateMin * 100f);
        public static int MaxTaxRatePercent => Mathf.RoundToInt(TaxRateMax * 100f);
        public static int SafeMinPercent => Mathf.RoundToInt(SafeMin * 100f);
        public static int SafeMaxPercent => Mathf.RoundToInt(SafeMax * 100f);

        public static int ClampRate(int rate)
        {
            int min = MinTaxRatePercent;   // -1000%
            int max = MaxTaxRatePercent;   // +100%

            if (ClampToSafeLimits)
            {
                min = Mathf.Max(min, SafeMinPercent);
                max = Mathf.Min(max, SafeMaxPercent);
            }

            if (min > max)
            {
                int tmp = min;
                min = max;
                max = tmp;
            }

            return Mathf.Clamp(rate, min, max);
        }

        /// <summary>附加税率范围（百分比）：-1000 .. +100。旧版 GetZoneRange(TaxAreaType) 的
        /// 区域参数从未实现差异化，1.5.0 起改为无参。</summary>
        public static int2 GetRange()
        {
            return new int2(MinTaxRatePercent, MaxTaxRatePercent);
        }
    }
}
