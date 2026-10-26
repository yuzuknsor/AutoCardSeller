# AutoCardSeller

[中文](README.md) | English

> For learning purposes only. Evaluate the risks yourself before use — you are solely responsible for any consequences.

AutoCardSeller is an [ArchiSteamFarm (ASF)](https://github.com/JustArchiNET/ArchiSteamFarm) plugin that automatically sells Steam trading cards.

It fully reuses ASF's secure session stack (login, session refresh, request throttling) and anchors pricing at the **current lowest market listing**. The goal is to sell cards **quickly**, not to maximize profit.

## Features

- Scans inventory for regular trading cards and lists them on the market automatically
- Foil trading cards are **skipped** by default, left for manual handling
- Pricing anchored to the current lowest market listing, with configurable undercut
- Read-only `preview` shows **reference price / listing price / net proceeds** per card
- Optional scheduled auto-sell
- Configurable minimum net price, per-card keep count, and price-history window

## Installation

1. Build the plugin, then put `AutoCardSeller.dll` into ASF's `plugins/AutoCardSeller/` directory
2. Restart ASF — `AutoCardSellerPlugin loaded` in the log means you are done

## Configuration

Add the following fields to the bot's config file (e.g. `config/KNSOR.json`):

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `AutoCardSellerEnabled` | bool | false | Enable the plugin |
| `AutoCardSellerAutoSell` | bool | false | Sell automatically on a schedule after login |
| `AutoCardSellerAutoSellIntervalHours` | uint | 24 | Auto-sell interval in hours |
| `AutoCardSellerMinPriceCents` | uint | 3 | Minimum net proceeds (cents) — skip below this |
| `AutoCardSellerKeepCount` | uint | 0 | Cards to keep per type |
| `AutoCardSellerHistoryDays` | uint | 7 | Price-history lookback window (days) |
| `AutoCardSellerUndercutPercent` | uint | 0 | Undercut the lowest listing by N% |
| `AutoCardSellerFeePercent` | uint | 15 | Steam fee rate (%), used to estimate net proceeds and enforce the minimum |

Example:

```json
{
	"AutoCardSellerEnabled": true,
	"AutoCardSellerUndercutPercent": 3
}
```

## Commands

| Command | Access | Description |
|---------|--------|-------------|
| `sellcards preview` | Master | Query prices only — lists reference/listing/net price per card without listing anything |
| `sellcards` | Master | Scan inventory and list cards for sale at the computed prices |

## Pricing Logic

1. Query `priceoverview` for the current **lowest market listing** (buyer-paid price; localized decimal formats for all wallet currencies are parsed correctly)
2. **Listing price** = lowest × (1 − undercut), i.e. the buyer-paid price passed to `sellitem`
3. **Net proceeds** = listing price × (1 − fee rate) — estimation only, used for display and the minimum-price guard
4. Falls back to the average price history when no lowest listing is available
5. A 0.5 s delay between card types reduces the chance of market rate limiting

## Building

- .NET 10 SDK required
- References ASF's `ArchiSteamFarm.dll` and `SteamKit2.dll` (see `HintPath` in the `.csproj`)
- Download the generic package from [ASF Releases](https://github.com/JustArchiNET/ArchiSteamFarm/releases) and put those dlls into a `refs/` folder **next to** the repository root (matching the `HintPath`, which also keeps them out of git)
- Build with `dotnet build -c Release -o out`

## License

[Apache License 2.0](LICENSE)

Dependencies: [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) (Apache-2.0), [SteamKit2](https://github.com/SteamRE/SteamKit) (LGPL-2.1, compile-time reference only, not distributed)

## Disclaimer

This plugin automates Steam Market operations and may violate the Steam Subscriber Agreement. Use it only if you understand and accept the risks — you are solely responsible for any consequences.
