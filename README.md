# HealingHands

A server-side Vintage Story mod (1.21) that changes how healing items behave when one
player uses them on **another** player, based on the healer's character traits.

A skilled healer can restore more health, finish the heal faster, and apply the bandage more
quickly; a clumsy or frail one does the opposite. Healing yourself is never affected — the mod
only acts when the target is a different player. No client installation is required.

---

## What gets modified

Every heal from one player to another has three parts, and HealingHands can scale each one
independently:

| Part | Config field | What it controls |
|---|---|---|
| **Health restored** | `HpMultiplier` | The total HP the item gives the target. |
| **Healing speed** | `HealSpeedMultiplier` | How quickly the heal-over-time ticks resolve once applied. The *same* total HP is delivered, just faster or slower. |
| **Application speed** | `ApplySpeedMultiplier` | How long the healer must hold the item before the heal fires (the cast time). |

For each one, a multiplier of **1.0 means no change**, **above 1.0 is an improvement** (more
HP, faster ticks, faster cast), and **below 1.0 is a penalty**.

> Application speed is implemented through the vanilla `healingeffectivness` stat, which the
> game already uses to decide cast time. The mod adds its contribution on top of that stat for
> the duration of the cast, so it stacks with — rather than overwrites — any bonus the healer
> already has from armor, food, or other sources. (`healingeffectivness` is spelled exactly
> like that in vanilla, typo included.)

---

## Defaults vs. traits

For any heal, the mod chooses which multipliers to use in one of two ways:

- **The healer has none of the configured traits** → the **`Defaults`** block is used. Out of
  the box that's a flat **+25%** to all three parameters, so even an untraited player heals
  others noticeably better than the item's base numbers.
- **The healer has one or more configured traits** → the `Defaults` are ignored entirely, and
  only the matching traits' values are used (combined together if there's more than one — see
  below).

---

## Compounding modes

When a healer has **several** matching traits, their multipliers have to be merged into one
value per parameter. There are **three** modes, set by `CompoundingMode`:

| Mode | Formula | Behaviour |
|---|---|---|
| **`Additive`** *(default)* | `1 + (m₁−1) + (m₂−1) + … + (mₙ−1)` | Each trait's distance from 1.0 is summed. A +25% trait and a −15% trait net to **+10%**. Bonuses and penalties cancel out linearly, so stacking many traits stays controlled. |
| **`Multiplicative`** | `m₁ × m₂ × … × mₙ` | The multipliers chain. ×1.25 and ×1.15 give **×1.4375**. Bonuses compound on each other, so stacking grows faster than additive. |
| **`Highest`** | `max(m₁, m₂, …, mₙ)` | No stacking at all — only the single largest multiplier for each parameter is used. |

After compounding, every result is clamped between `MinMultiplier` and `MaxMultiplier`.

**Worked example** — a healer with `mender` (HP ×1.25) and `frail` (HP ×0.65), default
`Additive` mode:

```
1 + (1.25 − 1) + (0.65 − 1) = 1 + 0.25 − 0.35 = 0.90   →  10% less HP restored
```

Switch to `Multiplicative` and the same pair gives `1.25 × 0.65 = 0.8125`; switch to `Highest`
and it gives `1.25` (the larger of the two).

---

## Configuration

The config is written to `ModConfig/healinghands.json` on first run and can be edited freely.
Run `/healinghands reload` to apply changes without a restart; they take effect on the next
heal.

```json
{
  "Enabled": true,
  "CompoundingMode": "Additive",
  "MinMultiplier": 0.05,
  "MaxMultiplier": 10.0,
  "Defaults": {
    "HpMultiplier": 1.25,
    "HealSpeedMultiplier": 1.25,
    "ApplySpeedMultiplier": 1.25
  },
  "TraitModifiers": [
    {
      "TraitCode": "hardy",
      "Comment": "Robust constitution.",
      "Values": {
        "HpMultiplier": 1.45,
        "HealSpeedMultiplier": 1.35,
        "ApplySpeedMultiplier": 1.00
      }
    }
  ]
}
```

### Field reference

| Field | Meaning |
|---|---|
| `Enabled` | Master switch. When `false`, the mod does nothing and every heal uses the item's base values. |
| `CompoundingMode` | How multiple matching traits combine: `Additive`, `Multiplicative`, or `Highest` (see above). |
| `MinMultiplier` / `MaxMultiplier` | Lower and upper clamps applied to every final multiplier after compounding. Keep extreme trait stacks in check. |
| `Defaults` | The three multipliers used when the healer has **none** of the listed traits. |
| `TraitModifiers` | The list of traits that change healing. Each entry has a `TraitCode`, an optional `Comment`, and a `Values` block. |

Each `Values` block (and the `Defaults` block) holds the three multipliers `HpMultiplier`,
`HealSpeedMultiplier`, and `ApplySpeedMultiplier`, described in the table near the top.

> There is intentionally **no** "only affect other-player heals" option. The mod's entire job
> is healing applied to another player — self-heals and non-player targets are skipped in code,
> so the only switch needed is `Enabled`.

### Trait codes

`TraitCode` must match a key in the healer's `WatchedAttributes["traits"]`. Vanilla codes come
from `assets/game/config/characterclasses.json`. **Modded traits work the same way** — any
trait stored under that key can be listed. Codes that no player has are simply ignored, so
listing a trait that isn't present is harmless.

The bundled config ships with ten vanilla traits, five positive and five negative:

| Trait | Type | HP | Healing speed | Application speed |
|---|---|---|---|---|
| `hardy` | positive | +45% | +35% | — |
| `resourceful` | positive | +55% | — | — |
| `soldier` | positive | +25% | — | +55% |
| `mender` | positive | +25% | +25% | +65% |
| `furtive` | positive | — | — | +45% |
| `frail` | negative | −35% | −20% | — |
| `weak` | negative | −30% | −30% | — |
| `ravenous` | negative | −20% | −10% | −20% |
| `kind` | negative | — | — | −35% |
| `nervous` | negative | — | — | −45% |

---

## Commands

Both require the `commandplayer` privilege.

| Command | Description |
|---|---|
| `/healinghands checktraits <player>` | Lists the configured traits the player has and the combined modifier they would produce when healing another player. |
| `/healinghands reload` | Reloads `ModConfig/healinghands.json` from disk. |

---

## Building

This is the **1.21** branch. Copy `Properties/localSettings.props.template` to
`Properties/localSettings.props`, set your Vintage Story install path, then:

```sh
dotnet build HealingHands_1.21.csproj
dotnet build HealingHands_1.21.csproj -c Release
```

---

## How it works internally

Two behaviors cooperate, and neither mutates the shared healing-item definition:

- **`HealingInterceptBehavior`** is prepended onto every healing item. Prepending is required
  because vanilla's healing behavior halts the behavior loop once it runs, so anything added
  after it would never fire. In `Start` it sets the temporary `healingeffectivness` stat for
  application speed; in `Stop` it hands the resolved modifier to the target just before the
  heal is applied; in both it schedules the stat's removal on the next tick (guarded so a
  quickly-restarted cast doesn't lose its stat).
- **`HealReceiveBehavior`** sits on every player. When a heal lands and a modifier has been
  handed to it, it scales the HP amount and heal-over-time duration before the health system
  applies them.

The hand-off between the two happens synchronously on the server thread, so exactly one
modifier is ever in flight per target, and separate players healing separate targets never
interfere with each other.
