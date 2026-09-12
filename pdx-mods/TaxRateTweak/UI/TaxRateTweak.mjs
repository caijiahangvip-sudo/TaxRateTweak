/*
 * Cities: Skylines II UI Module
 * Id: TaxRateTweak
 * Author: TaxRateTweak
 * Version: 1.8.28
 * Dependencies:
 */

const React = window.React;
const { bindValue, useValue } = window["cs2/api"];
const demandBinding = bindValue("TaxRateTweak", "densityDemand", {
    enabled: false, values: [0, 0, 0, 0, 0], unlocked: [false, false, false, false, false],
    factors: [[], [], [], [], []]
});
const demandPage = "game-ui/game/components/city-info-panel/city-info-demand/demand-page.tsx";
const demandSection = "game-ui/game/components/city-info-panel/city-info-demand/demand-section/demand-section.tsx";
const cityInfoField = "game-ui/game/components/toolbar/top/city-info-field/";
const Selection = React.createContext(null);
const keys = ["TaxRateTweakLow", "TaxRateTweakRow", "TaxRateTweakMedium", "TaxRateTweakHigh", "TaxRateTweakLowRent"];
const fraction = value => Math.max(0, Math.min(1, value / 100));

export const hasCSS = false;

function registerDemand(registry) {
    for (const [path, name] of [
        [demandPage, "CityInfoDemand"], [demandSection, "DemandSection"],
        [cityInfoField + "demand-bars.tsx", "DemandBars"],
        [cityInfoField + "city-info-field.tsx", "CityInfoField"]
    ]) {
        if (!registry.get(path, name)) throw new Error("Missing native UI export: " + path + "#" + name);
    }
    const types = registry.get(demandPage, "DemandType");
    const colors = registry.get(demandPage, "demandColors");
    const icons = registry.get(demandPage, "demandIcons");
    const fieldStyles = registry.get(cityInfoField + "city-info-field.module.scss", "classes");

    function addType(name, color, icon) {
        const value = types[name] ?? Math.max(...Object.values(types).filter(value => typeof value === "number")) + 1;
        types[name] = value;
        types[value] = name;
        colors[value] = color;
        icons[value] = "Media/Game/Icons/" + icon;
        return value;
    }

    const rowType = addType("TaxRateTweakRow", "#56C878", "ZoneResidentialMediumRow.svg");
    const lowRentType = addType("TaxRateTweakLowRent", "#81AE68", "ZoneResidentialLowRent.svg");
    const residentialTypes = [types.ResidentialLow, rowType, types.ResidentialMedium, types.ResidentialHigh, lowRentType];
    const vanillaTypes = [types.ResidentialLow, types.ResidentialMedium, types.ResidentialHigh];
    const vanillaKeys = ["ResidentialLow", "ResidentialMedium", "ResidentialHigh"];

    registry.extend(demandPage, "CityInfoDemand", Original => props => {
        const [selected, select] = React.useState(types.ResidentialLow);
        return React.createElement(Selection.Provider, { value: { selected, select } }, React.createElement(Original, props));
    });

    function ResidentialSection({ Original, sourceProps, category }) {
        const state = useValue(demandBinding);
        const selection = React.useContext(Selection);
        const type = residentialTypes[category];
        if (!state.enabled) {
            return category === 1 || category === 4 ? null : React.createElement(Original, sourceProps);
        }
        const classNames = (sourceProps.className || "").split(/\s+/).filter(name => name !== "selected");
        if (selection?.selected === type) classNames.push("selected");
        return React.createElement(Original, {
            ...sourceProps,
            type,
            demand: fraction(state.values[category]),
            unlocking: { ...sourceProps.unlocking, locked: !state.unlocked[category] },
            factors: state.factors[category],
            className: classNames.join(" "),
            onSelect: selected => {
                selection?.select(selected);
                sourceProps.onSelect(selected);
            }
        });
    }

    registry.extend(demandSection, "DemandSection", Original => props => {
        const selection = React.useContext(Selection);
        const category = residentialTypes.indexOf(props.type);
        if (category < 0) return React.createElement(Original, {
            ...props,
            onSelect: selected => {
                selection?.select(selected);
                props.onSelect(selected);
            }
        });
        const section = React.createElement(ResidentialSection, { Original, sourceProps: props, category });
        if (category === 0 || category === 3) {
            return React.createElement(React.Fragment, null, section,
                React.createElement(ResidentialSection, { Original, sourceProps: props, category: category + 1 }));
        }
        return section;
    });

    registry.extend(cityInfoField + "demand-bars.tsx", "DemandBars", Original => props => {
        const state = useValue(demandBinding);
        if (!state.enabled || !props.items.some(item => item.key === "ResidentialLow"))
            return React.createElement(Original, props);
        const items = [];
        for (const item of props.items) {
            if (item.key === "ResidentialLow") {
                for (let category = 0; category < residentialTypes.length; category++) {
                    if (state.unlocked[category]) items.push({
                        key: keys[category], color: colors[residentialTypes[category]], value: fraction(state.values[category])
                    });
                }
            } else if (!vanillaKeys.includes(item.key)) {
                items.push(item);
            }
        }
        return React.createElement(Original, { ...props, items });
    });

    function extendLegend(element, state) {
        if (!React.isValidElement(element)) return element;
        if (element.props.className === fieldStyles.legend) {
            const children = [];
            for (const child of React.Children.toArray(element.props.children)) {
                if (child.props?.type === types.ResidentialLow) {
                    for (let category = 0; category < residentialTypes.length; category++) {
                        if (state.unlocked[category]) children.push(React.cloneElement(child, {
                            type: residentialTypes[category], key: keys[category]
                        }));
                    }
                } else if (!vanillaTypes.includes(child.props?.type)) {
                    children.push(child);
                }
            }
            return React.cloneElement(element, {}, children);
        }
        const updates = {};
        for (const property of ["children", "content"]) {
            if (element.props[property] != null) {
                const value = element.props[property];
                updates[property] = Array.isArray(value) ? value.map(child => extendLegend(child, state)) : extendLegend(value, state);
            }
        }
        return Object.keys(updates).length ? React.cloneElement(element, updates) : element;
    }

    registry.extend(cityInfoField + "city-info-field.tsx", "CityInfoField", Original => {
        if (typeof Original !== "function") return props => React.createElement(Original, props);
        return props => {
            const state = useValue(demandBinding);
            const element = Original(props);
            return state.enabled ? extendLegend(element, state) : element;
        };
    });

    console.info("TaxRateTweak 1.8.28: five residential demand sections, toolbar bars and legends registered.");
}

export default function register(registry) {
    try {
        registerDemand(registry);
    } catch (error) {
        console.error("TaxRateTweak: native demand UI integration failed; keeping the original interface.", error);
    }
}
