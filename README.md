# TaxRateTweak

Source code for **TaxRateTweak**, a mod for *Cities: Skylines II* — published on PDX Mods (mod id `155720`).

The mod source lives in [`pdx-mods/TaxRateTweak/`](pdx-mods/TaxRateTweak/). This repository tracks only that mod.

## What it does

- Five residential density tax rates (low density / row homes / medium / high / low-rent) added to the native taxation panel.
- Each row shows its own surcharge; a household's combined residential rate is the base rate plus that row's value.
- Tax-rate limits widened from `-10%..+30%` to `-1000%..+100%` (a negative rate is a subsidy).
- Housing demand per density: each tier is counted and evaluated separately, and a tier's rate only moves its own demand.
- **Tax Exemption Threshold** city policy: income below the threshold pays no residential tax at all — base rate and density surcharge together. It only exempts, it never pays out.
- Density surcharges feed the vanilla "Tax" happiness factor with the same formula and sensitivity as a rate change.
- Every amount settles as real money from household wallets into the city treasury, and forecasts appear in the native panel.
- Native-style: the mod extends the existing systems and replaces no UI.

Localized into the game's supported languages: English, Simplified Chinese, Traditional Chinese, German, Spanish, French, Italian, Japanese, Korean, Polish, Brazilian Portuguese and Russian.

## Layout

| Path | Contents |
| --- | --- |
| `Mod.cs` | Mod entry point: Harmony patches, system registration, localization injection |
| `Patches/` | Harmony patches into the native taxation, UI, tax-estimate and demand systems |
| `Systems/` | ECS systems: per-density settlement, happiness, demand, estimates, policy |
| `Services/` | Density catalog, demand model, localization, estimate refresh state |
| `UI/TaxRateTweak.mjs` | Front-end module bound into the native UI |
| `Settings.cs` | Mod settings |
| `Properties/PublishConfiguration.xml` | PDX Mods publishing metadata |
| `README.md` | Development log and release notes (Chinese) |

## Building

Requirements: the .NET SDK, Node.js (only for the UI syntax check), and the game's modding toolchain with the `CSII_TOOLPATH` environment variable pointing at it.

```powershell
cd pdx-mods/TaxRateTweak
dotnet restore TaxRateTweak.csproj --locked-mode
node --check UI/TaxRateTweak.mjs
dotnet build TaxRateTweak.csproj -c Release
```

The build copies its output (including `TaxRateTweak.mjs` and the HarmonyX / Mono.Cecil / MonoMod dependencies) into `C:\cs2-mod-factory\pdx-staging\TaxRateTweak`, which is the PDX Mods publishing staging directory. It is deliberately not deployed to the local game `Mods` folder.

Game assemblies are not part of this repository — they are referenced from the installed game through the toolchain.

## Third-party components

HarmonyX, Mono.Cecil, MonoMod.Utils and MonoMod.RuntimeDetour, all pinned for `netstandard2.0`. See [`THIRD_PARTY_NOTICES.txt`](pdx-mods/TaxRateTweak/THIRD_PARTY_NOTICES.txt).

## License

[MIT](LICENSE).

Not affiliated with or endorsed by Colossal Order or Paradox Interactive. *Cities: Skylines II* and its assets belong to their respective owners.
