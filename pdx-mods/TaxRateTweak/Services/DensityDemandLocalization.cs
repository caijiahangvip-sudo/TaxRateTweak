using System;
using System.Collections.Generic;
using Colossal.Localization;
using Game.SceneFlow;

namespace TaxRateTweak.Services
{
    public static class DensityDemandLocalization
    {
        private static readonly string[] SupportedLocales =
        {
            "de-DE", "en-US", "es-ES", "fr-FR", "it-IT", "ja-JP", "ko-KR", "pl-PL", "pt-BR", "ru-RU", "zh-HANS", "zh-HANT"
        };

        public static void Register()
        {
            try
            {
                foreach (string locale in SupportedLocales)
                {
                    bool traditional = locale == "zh-HANT";
                    bool simplified = locale == "zh-HANS";
                    string row = "Row-Home Demand", lowRent = "Low-Rent Housing Demand";
                    string positive = "Lower residential surtax", negative = "Higher residential surtax";
                    string rowDescription = "Independent row-home supply, vacancies and demand, used directly for selecting and spawning row homes.";
                    string lowRentDescription = "Independent low-rent supply, vacancies and demand, used directly for selecting and spawning low-rent housing.";
                    if (locale == "de-DE") { row = "Reihenhaus-Nachfrage"; lowRent = "Nachfrage nach günstigem Wohnraum"; positive = "Niedrigerer Wohnzuschlag"; negative = "Höherer Wohnzuschlag"; rowDescription = "Eigenständiges Angebot, Leerstände und Nachfrage nach Reihenhäusern für Auswahl und Bau von Reihenhäusern."; lowRentDescription = "Eigenständiges Angebot, Leerstände und Nachfrage nach günstigem Wohnraum für Auswahl und Bau."; }
                    else if (locale == "es-ES") { row = "Demanda de casas adosadas"; lowRent = "Demanda de alquiler asequible"; positive = "Recargo residencial menor"; negative = "Recargo residencial mayor"; rowDescription = "La oferta, las vacantes y la demanda de casas adosadas se usan directamente para seleccionar y construirlas."; lowRentDescription = "La oferta, las vacantes y la demanda de alquiler asequible se usan directamente para seleccionar y construir viviendas."; }
                    else if (locale == "fr-FR") { row = "Demande de maisons mitoyennes"; lowRent = "Demande de logements abordables"; positive = "Surtaxe résidentielle réduite"; negative = "Surtaxe résidentielle élevée"; rowDescription = "L’offre, les logements vacants et la demande de maisons mitoyennes servent directement à leur sélection et construction."; lowRentDescription = "L’offre, les logements vacants et la demande de logements abordables servent directement à leur sélection et construction."; }
                    else if (locale == "it-IT") { row = "Domanda di case a schiera"; lowRent = "Domanda di alloggi a basso costo"; positive = "Supplemento residenziale ridotto"; negative = "Supplemento residenziale elevato"; rowDescription = "Offerta, alloggi vuoti e domanda di case a schiera usati direttamente per selezionarle e costruirle."; lowRentDescription = "Offerta, alloggi vuoti e domanda di alloggi a basso costo usati direttamente per selezionarli e costruirli."; }
                    else if (locale == "ja-JP") { row = "長屋住宅の需要"; lowRent = "低家賃住宅の需要"; positive = "住宅付加税が低い"; negative = "住宅付加税が高い"; rowDescription = "長屋住宅の供給、空室、需要を個別に計算し、長屋住宅の選択と建設に使用します。"; lowRentDescription = "低家賃住宅の供給、空室、需要を個別に計算し、低家賃住宅の選択と建設に使用します。"; }
                    else if (locale == "ko-KR") { row = "연립 주택 수요"; lowRent = "저렴한 임대 주택 수요"; positive = "주거 추가세 낮음"; negative = "주거 추가세 높음"; rowDescription = "연립 주택의 공급, 공실 및 수요를 별도로 계산하여 선택과 건설에 사용합니다."; lowRentDescription = "저렴한 임대 주택의 공급, 공실 및 수요를 별도로 계산하여 선택과 건설에 사용합니다."; }
                    else if (locale == "pl-PL") { row = "Popyt na domy szeregowe"; lowRent = "Popyt na tanie mieszkania"; positive = "Niższy dodatek mieszkaniowy"; negative = "Wyższy dodatek mieszkaniowy"; rowDescription = "Podaż, pustostany i popyt na domy szeregowe są używane bezpośrednio do ich wyboru i budowy."; lowRentDescription = "Podaż, pustostany i popyt na tanie mieszkania są używane bezpośrednio do ich wyboru i budowy."; }
                    else if (locale == "pt-BR") { row = "Demanda por casas geminadas"; lowRent = "Demanda por aluguel acessível"; positive = "Sobretaxa residencial menor"; negative = "Sobretaxa residencial maior"; rowDescription = "A oferta, as vagas e a demanda por casas geminadas são usadas diretamente para selecioná-las e construí-las."; lowRentDescription = "A oferta, as vagas e a demanda por aluguel acessível são usadas diretamente para selecionar e construir moradias."; }
                    else if (locale == "ru-RU") { row = "Спрос на таунхаусы"; lowRent = "Спрос на доступное жильё"; positive = "Низкая надбавка на жильё"; negative = "Высокая надбавка на жильё"; rowDescription = "Предложение, вакансии и спрос на таунхаусы напрямую используются для их выбора и строительства."; lowRentDescription = "Предложение, вакансии и спрос на доступное жильё напрямую используются для его выбора и строительства."; }
                    Add(locale,
                        simplified ? "联排住宅需求" : traditional ? "連排住宅需求" : row,
                        simplified ? "廉租住宅需求" : traditional ? "廉租住宅需求" : lowRent,
                        simplified ? "联排住宅有独立的住房供给、空置量及需求，直接用于联排住宅的选址与新建。" : traditional ? "連排住宅有獨立的住房供給、空置量及需求，直接用於連排住宅的選址與新建。" : rowDescription,
                        simplified ? "廉租房有独立的住房供给、空置量及需求，直接用于廉租房的选址与新建。" : traditional ? "廉租房有獨立的住房供給、空置量及需求，直接用於廉租房的選址與新建。" : lowRentDescription,
                        simplified ? "本类住宅附加税较低" : traditional ? "本類住宅附加稅較低" : positive,
                        simplified ? "本类住宅附加税较高" : traditional ? "本類住宅附加稅較高" : negative);
                }
            }
            catch (Exception exception)
            {
                Mod.log.Warn($"TaxRateTweak: demand localization failed: {exception.Message}");
            }
        }

        private static void Add(string locale, string row, string lowRent, string rowDescription,
            string lowRentDescription, string positive, string negative)
        {
            GameManager.instance.localizationManager.AddSource(locale, new MemorySource(new Dictionary<string, string>
            {
                ["CityInfoPanel.DEMAND_TITLE[TaxRateTweakRow]"] = row,
                ["CityInfoPanel.DEMAND_TITLE[TaxRateTweakLowRent]"] = lowRent,
                ["CityInfoPanel.DEMAND_DESCRIPTION[TaxRateTweakRow]"] = rowDescription,
                ["CityInfoPanel.DEMAND_DESCRIPTION[TaxRateTweakLowRent]"] = lowRentDescription,
                ["CityInfoPanel.DEMAND_FACTOR_POSITIVE[TaxRateTweakDensityTax]"] = positive,
                ["CityInfoPanel.DEMAND_FACTOR_NEGATIVE[TaxRateTweakDensityTax]"] = negative
            }));
        }
    }
}
