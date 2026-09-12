using System;
using System.Collections.Generic;
using Colossal.Localization;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using HarmonyLib;

namespace TaxRateTweak
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(Mod)}").SetShowsErrorsInUI(false);
        private Harmony _harmony;
        public static bool FiveDensityReady;
        // 两个五类需求补丁是否都成功挂上。用于区分"补丁没挂上"与"运行期异常"：
        // 前者不能重试，后者可以，避免一次异常把整个功能永久关掉。
        public static bool FiveDensityPatchesApplied;

        public void OnLoad(UpdateSystem updateSystem)
        {
            Settings.EnsureDefaults();
            Settings.Load();

            if (!Settings.Enabled)
            {
                log.Info("TaxRateTweak (PDX) disabled by configuration.");
                return;
            }

            _harmony = new Harmony("TaxRateTweak.PDX");
            // 逐类打补丁并单独捕获失败：游戏更新导致某个 patch 目标（尤其是 TaxationUISystem
            // 的私有方法）变动时，只降级对应功能，不拖垮整个模组。
            PatchSafely(typeof(Patches.TaxSystemPatch));
            PatchSafely(typeof(Patches.TaxationUISystemPatch));
            // 注意 && 短路：ZoneSpawnDemandPatch 失败时 ResidentialDemandPatch 不会打补丁
            // （两者是一套东西，单独挂任一个都没有意义）。
            FiveDensityReady = PatchSafely(typeof(Patches.ZoneSpawnDemandPatch)) &&
                PatchSafely(typeof(Patches.ResidentialDemandPatch));
            FiveDensityPatchesApplied = FiveDensityReady;
            PatchSafely(typeof(Patches.DensityTaxPanelPatch));
            PatchSafely(typeof(Patches.TaxEstimatePatch));

            InjectDensityLabels();
            InjectPolicyLabels();
            Services.DensityDemandLocalization.Register();

            // 主菜单阶段就创建住宅免税线政策 prefab：存档加载时的 PrefabID 重映射
            // 要求政策实体在读档前已存在。
            try
            {
                var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
                Systems.MinimumEarningsPolicySystem.EnsureCreated(
                    world.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>(), world.EntityManager);
            }
            catch (Exception e)
            {
                log.Warn($"TaxRateTweak: policy prefab creation deferred to in-game: {e.Message}");
            }

            // 注册分密度附加税结算系统（游戏模拟阶段）。
            updateSystem.UpdateAt<Systems.DensityTaxSystem>(SystemUpdatePhase.GameSimulation);
            // 注册密度税预估刷新器（UIUpdate 阶段：暂停时 GameSimulation 停摆，但面板预估要照常刷新）。
            updateSystem.UpdateAt<Systems.DensityEstimateSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<Systems.DensityDemandUISystem>(SystemUpdatePhase.UIUpdate);
            // 注册住宅免税线政策系统（创建政策 prefab + 同步政策值到设置）。
            updateSystem.UpdateAt<Systems.MinimumEarningsPolicySystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<Systems.FiveDensityDemandSystem>(SystemUpdatePhase.GameSimulation);
            // 注册幸福度注入：把人口加权平均附加税写进城市的税收幸福度修正，
            // 排在 CityModifierUpdateSystem 之后、CitizenHappinessSystem 之前（见该类注释）。
            updateSystem.UpdateAt<Systems.DensityTaxHappinessSystem>(SystemUpdatePhase.GameSimulation);

            log.Info("TaxRateTweak (PDX) loaded, patches applied.");
        }

        public void OnDispose()
        {
            FiveDensityReady = false;
            Settings.Save();
            _harmony?.UnpatchSelf();
        }

        private bool PatchSafely(Type patchClass)
        {
            try
            {
                _harmony.PatchAll(patchClass);
                return true;
            }
            catch (Exception e)
            {
                // 用 Error 级别：补丁失败 = 对应功能整块消失（例如密度税行从前端消失），
                // 曾在 1.8.7 被当成普通告警漏看。这里连堆栈一起记，便于对照 Game.dll 的签名。
                log.Error($"TaxRateTweak: patch {patchClass.Name} FAILED, feature disabled: {e}");
                return false;
            }
        }

        /// <summary>
        /// 给原生税收面板住宅区的索引标签补第 6-10 项（索引 5-9 = 五个密度档附加税）。
        /// 原生 TAXATION_RESIDENTIAL_SLIDER_JOBLEVEL 只有 :0..:4 五个索引词条，
        /// MemorySource 会把该键的 indexCounts 扩到 10，前端按索引取到这些行标签。
        /// </summary>
        private static void InjectDensityLabels()
        {
            const string keyFormat = "EconomyPanel.TAXATION_RESIDENTIAL_SLIDER_JOBLEVEL:{0}";
            var zhHans = new[] { "低密度住宅", "联排住宅", "中密度住宅", "高密度住宅", "廉租房" };
            var zhHant = new[] { "低密度住宅", "連排住宅", "中密度住宅", "高密度住宅", "廉租房" };
            var enUS = new[] { "Low Density", "Row Homes", "Medium Density", "High Density", "Low Rent" };
            try
            {
                var lm = GameManager.instance.localizationManager;
                AddDensitySource(lm, "zh-HANS", keyFormat, zhHans);
                AddDensitySource(lm, "zh-HANT", keyFormat, zhHant);
                AddDensitySource(lm, "en-US", keyFormat, enUS);
                var localized = new Dictionary<string, string[]>
                {
                    ["de-DE"] = new[] { "Niedrige Dichte", "Reihenhäuser", "Mittlere Dichte", "Hohe Dichte", "Günstiger Wohnraum" },
                    ["es-ES"] = new[] { "Baja densidad", "Casas adosadas", "Densidad media", "Alta densidad", "Alquiler asequible" },
                    ["fr-FR"] = new[] { "Faible densité", "Maisons mitoyennes", "Densité moyenne", "Haute densité", "Loyers modérés" },
                    ["it-IT"] = new[] { "Bassa densità", "Case a schiera", "Media densità", "Alta densità", "Affitti bassi" },
                    ["ja-JP"] = new[] { "低密度住宅", "長屋住宅", "中密度住宅", "高密度住宅", "低家賃住宅" },
                    ["ko-KR"] = new[] { "저밀도 주거", "연립 주택", "중밀도 주거", "고밀도 주거", "저렴한 임대 주택" },
                    ["pl-PL"] = new[] { "Niska gęstość", "Domy szeregowe", "Średnia gęstość", "Wysoka gęstość", "Tanie mieszkania" },
                    ["pt-BR"] = new[] { "Baixa densidade", "Casas geminadas", "Média densidade", "Alta densidade", "Aluguel acessível" },
                    ["ru-RU"] = new[] { "Низкая плотность", "Таунхаусы", "Средняя плотность", "Высокая плотность", "Доступное жильё" }
                };
                foreach (var entry in localized) AddDensitySource(lm, entry.Key, keyFormat, entry.Value);
            }
            catch (Exception e)
            {
                log.Warn($"TaxRateTweak: density label injection failed: {e.Message}");
            }
        }

        private static void AddDensitySource(LocalizationManager lm, string locale, string keyFormat, string[] labels)
        {
            var dict = new Dictionary<string, string>();
            for (int i = 0; i < labels.Length; i++)
            {
                dict[string.Format(keyFormat, 5 + i)] = labels[i];
            }
            lm.AddSource(locale, new MemorySource(dict));
        }

        /// <summary>
        /// 给免税线政策注入本地化词条（政策面板左列标题 + 右侧描述）。
        /// 键名 = Policy.TITLE/DESCRIPTION[政策 prefab 名]，与原生政策同一套约定。
        /// 显示名按用户要求用口语"免税线"（描述里说明只影响住宅税）。
        /// </summary>
        private static void InjectPolicyLabels()
        {
            string name = Systems.MinimumEarningsPolicySystem.kPolicyName;
            try
            {
                var lm = GameManager.instance.localizationManager;
                lm.AddSource("zh-HANS", new MemorySource(new Dictionary<string, string>
                {
                    [$"Policy.TITLE[{name}]"] = "免税线",
                    [$"Policy.DESCRIPTION[{name}]"] = "收入低于免税线的部分不缴住宅税——基础住宅税和分密度附加税一起免征，只免征、不发放补贴。拖动滚动条调整免税线。",
                }));
                lm.AddSource("zh-HANT", new MemorySource(new Dictionary<string, string>
                {
                    [$"Policy.TITLE[{name}]"] = "免稅線",
                    [$"Policy.DESCRIPTION[{name}]"] = "收入低於免稅線的部分不繳住宅稅——基礎住宅稅與分密度附加稅一起免征，只免征、不發放補貼。拖動捲動條調整免稅線。",
                }));
                lm.AddSource("en-US", new MemorySource(new Dictionary<string, string>
                {
                    [$"Policy.TITLE[{name}]"] = "Tax Exemption Threshold",
                    [$"Policy.DESCRIPTION[{name}]"] = "Income below this threshold is exempt from residential tax - both the base rate and the density surtax. It only exempts, it never pays out. Drag the slider to adjust the threshold.",
                }));
                foreach (string locale in new[] { "de-DE", "es-ES", "fr-FR", "it-IT", "ja-JP", "ko-KR", "pl-PL", "pt-BR", "ru-RU" })
                {
                    string title = "Tax Exemption Threshold";
                    string description = "Income below this threshold is exempt from residential tax - both the base rate and the density surtax. It only exempts, it never pays out. Drag the slider to adjust the threshold.";
                    if (locale == "de-DE") { title = "Steuerfreibetrag"; description = "Einkommen unterhalb dieses Schwellenwerts ist von der Wohnsteuer befreit - sowohl Grundsteuersatz als auch Zuschlag. Er befreit nur, er zahlt nie aus. Mit dem Schieberegler anpassen."; }
                    else if (locale == "es-ES") { title = "Umbral de exención fiscal"; description = "La renta por debajo de este umbral queda exenta del impuesto residencial: tanto el tipo base como el recargo. Solo exime, nunca paga. Ajusta el umbral con el control deslizante."; }
                    else if (locale == "fr-FR") { title = "Seuil d’exonération fiscale"; description = "Les revenus sous ce seuil sont exonérés de la taxe résidentielle : taux de base et surtaxe. Il exonère seulement, il ne verse jamais rien. Réglez le seuil avec le curseur."; }
                    else if (locale == "it-IT") { title = "Soglia di esenzione fiscale"; description = "Il reddito sotto questa soglia è esente dall'imposta residenziale: sia l'aliquota base sia il supplemento. Esonera soltanto, non paga mai. Regola la soglia con il cursore."; }
                    else if (locale == "ja-JP") { title = "住宅税の免税基準"; description = "この基準未満の所得は住宅税（基本税率と付加税）が免除されます。免除のみで、給付は行いません。スライダーで基準を調整します。"; }
                    else if (locale == "ko-KR") { title = "주거 세금 면제 기준"; description = "이 기준 미만의 소득은 주거세(기본 세율과 추가세)가 면제됩니다. 면제만 하며 지급은 하지 않습니다. 슬라이더로 기준을 조정하세요."; }
                    else if (locale == "pl-PL") { title = "Próg zwolnienia podatkowego"; description = "Dochód poniżej tego progu jest zwolniony z podatku mieszkaniowego: zarówno stawki podstawowej, jak i dodatku. Próg tylko zwalnia, nigdy nie wypłaca. Ustaw próg suwakiem."; }
                    else if (locale == "pt-BR") { title = "Limite de isenção fiscal"; description = "A renda abaixo deste limite é isenta do imposto residencial: tanto a alíquota base quanto a sobretaxa. Ele apenas isenta, nunca paga. Ajuste o limite pelo controle deslizante."; }
                    else if (locale == "ru-RU") { title = "Порог освобождения от налога"; description = "Доход ниже этого порога освобождается от жилищного налога: и базовой ставки, и надбавки. Порог только освобождает, но никогда не платит. Настройте порог ползунком."; }
                    lm.AddSource(locale, new MemorySource(new Dictionary<string, string>
                    {
                        [$"Policy.TITLE[{name}]"] = title,
                        [$"Policy.DESCRIPTION[{name}]"] = description,
                    }));
                }
            }
            catch (Exception e)
            {
                log.Warn($"TaxRateTweak: policy label injection failed: {e.Message}");
            }
        }
    }
}
