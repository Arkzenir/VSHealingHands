# HealingHands

A server-side Vintage Story mod that changes how healing items work when used on other players, based on the healer's character traits.

## Overview

When a player uses a healing item on another player, HealingHands checks the healer's traits against the configured list and adjusts three aspects of the heal:

- **HP amount** — how much health the item restores
- **Heal-over-time speed** — how quickly the ticks resolve after the item is applied
- **Application speed** — how long the healer must hold the item before it fires

Self-heals are never affected. The mod is entirely server-side and requires no client installation.

## Trait modifiers

Each entry in `TraitModifiers` maps a trait code to a set of multipliers. When the healer has none of the listed traits, the `Defaults` block is used instead. As soon as one or more listed traits are present, defaults are ignored and only the matching trait values apply — compounded if there are multiple.

### Compounding

| Mode | Behaviour |
|---|---|
| `Multiplicative` | Each multiplier chains: m₁ × m₂ × … × mN |
| `Additive` | Deltas from 1.0 sum: 1 + (m₁−1) + (m₂−1) + … |
| `Highest` | Only the single highest value per parameter applies |

`MinMultiplier` and `MaxMultiplier` clamp the final result after compounding.

### Multiplier reference

| Field | Effect of values above 1.0 | Effect of values below 1.0 |
|---|---|---|
| `HpMultiplier` | More HP restored | Less HP restored |
| `HealSpeedMultiplier` | HoT ticks arrive faster | HoT ticks arrive slower |
| `ApplySpeedMultiplier` | Item applies faster (shorter cast) | Item applies slower (longer cast) |

### healingeffectivness

Vintage Story tracks a `healingeffectivness` stat on every player (the vanilla typo is intentional — the game spells it this way). This stat already influences cast time through the vanilla item system. HealingHands feeds the `ApplySpeedMultiplier` into this same stat so the two systems work together naturally rather than independently.

## Default trait list

The shipped config covers five positive and five negative vanilla traits:

| Trait | Type | HP | HoT speed | Apply speed |
|---|---|---|---|---|
| `hardy` | positive | +20% | +15% | — |
| `mender` | positive | +10% | +10% | +30% |
| `resourceful` | positive | +25% | — | — |
| `soldier` | positive | +10% | — | +25% |
| `furtive` | positive | — | — | +20% |
| `frail` | negative | −20% | −10% | — |
| `nervous` | negative | — | — | −30% |
| `weak` | negative | −15% | −15% | — |
| `kind` | negative | — | — | −20% |
| `ravenous` | negative | −10% | −5% | −10% |

## Configuration

The config file is written to `ModConfig/healinghands.json` on first run and can be edited freely. Changes take effect immediately with `/healinghands reload` — no server restart needed.

```json
{
  "Enabled": true,
  "OnlyAffectOtherPlayerHeals": true,
  "CompoundingMode": "Multiplicative",
  "MinMultiplier": 0.05,
  "MaxMultiplier": 10.0,
  "Defaults": {
    "HpMultiplier": 1.0,
    "HealSpeedMultiplier": 1.0,
    "ApplySpeedMultiplier": 1.0
  },
  "TraitModifiers": [
    {
      "TraitCode": "hardy",
      "Values": {
        "HpMultiplier": 1.20,
        "HealSpeedMultiplier": 1.15,
        "ApplySpeedMultiplier": 1.00
      }
    }
  ]
}
```

Trait codes must exactly match the keys stored in `entity.WatchedAttributes["traits"]`. Vanilla codes come from `assets/game/config/characterclasses.json`. Modded trait codes work identically — any trait that follows the same storage convention is supported. Unrecognised codes are silently ignored.

## Commands

All commands require the `commandplayer` privilege.

| Command | Description |
|---|---|
| `/healinghands checktraits <player>` | Shows which configured traits the named player has and the combined modifier they produce |
| `/healinghands reload` | Reloads `ModConfig/healinghands.json` from disk |

## Building

Copy `Properties/localSettings.props.template` to `Properties/localSettings.props`, set your Vintage Story installation path, then:

```sh
dotnet build HealingHands_1.21.csproj
dotnet build HealingHands_1.22.csproj
dotnet build HealingHands_1.22.csproj -c Release
```
