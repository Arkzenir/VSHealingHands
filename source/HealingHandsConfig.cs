using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace HealingHands;

// ─────────────────────────────────────────────────────────────────────────────
// HealingHandsConfig — the root configuration object
//
// Deserialized from ModConfig/healinghands.json. Everything the server admin can
// tune lives here. The mod only ever acts when one player heals a DIFFERENT player;
// self-heals and non-player targets are filtered out in code, so there is no config
// switch for that — turning the mod off entirely is what `Enabled` is for.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Top-level configuration for HealingHands. Loaded from
/// <c>ModConfig/healinghands.json</c>; written with sensible defaults on first run.
/// </summary>
public class HealingHandsConfig
{
    /// <summary>
    /// Master on/off switch. When false, the mod performs no modification whatsoever and
    /// every heal uses the item's authored values. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Multipliers used when the healer has <em>none</em> of the traits listed in
    /// <see cref="TraitModifiers"/>. As soon as one or more listed traits match, these
    /// defaults are ignored completely and only the matched traits are used.
    /// <para>The shipped value is a flat +25% to all three parameters, so an untraited
    /// player still heals others noticeably better than the item's base numbers.</para>
    /// </summary>
    public HealingValues Defaults { get; set; } = new();

    /// <summary>
    /// One entry per trait that should change healing. Each entry binds a trait code to the
    /// three multipliers it contributes. When the healer has more than one matching trait,
    /// the matches are combined using <see cref="CompoundingMode"/>. Any trait code works —
    /// vanilla or modded; codes no player has are simply never matched.
    /// </summary>
    public List<TraitModifierConfig> TraitModifiers { get; set; } = [];

    /// <summary>
    /// How multiple matching trait multipliers are combined into one value per parameter.
    /// There are exactly three modes:
    /// <list type="bullet">
    ///   <item><term>Additive</term><description>(default) Sum each trait's distance from 1.0:
    ///   <c>1 + (m1−1) + (m2−1) + … + (mN−1)</c>. A +25% trait and a −15% trait net to +10%.
    ///   Bonuses and penalties cancel linearly, so stacking many traits stays controlled.</description></item>
    ///   <item><term>Multiplicative</term><description>Chain the multipliers:
    ///   <c>m1 × m2 × … × mN</c>. ×1.25 and ×1.15 give ×1.4375. Bonuses compound on each other,
    ///   so stacking grows faster than additive.</description></item>
    ///   <item><term>Highest</term><description>No stacking — take only the single largest
    ///   multiplier for each parameter across all matched traits.</description></item>
    /// </list>
    /// </summary>
    public CompoundingMode CompoundingMode { get; set; } = CompoundingMode.Additive;

    /// <summary>Lower clamp applied to every final combined multiplier, after compounding. Default: 0.05.</summary>
    public float MinMultiplier { get; set; } = 0.05f;

    /// <summary>Upper clamp applied to every final combined multiplier, after compounding. Default: 10.0.</summary>
    public float MaxMultiplier { get; set; } = 10.0f;

    // ─────────────────────────────────────────────────────────────────────────
    // Default config — written to disk when no config file exists yet
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the configuration written to <c>ModConfig/healinghands.json</c> on first run.
    /// The bundled traits are all vanilla character traits (from
    /// <c>assets/game/config/characterclasses.json</c>): five positive and five negative,
    /// chosen for their thematic fit to a healing role. The baseline is +25%, and the trait
    /// values are scaled around that baseline.
    /// </summary>
    public static HealingHandsConfig CreateDefault() => new()
    {
        Enabled         = true,
        CompoundingMode = CompoundingMode.Additive,
        MinMultiplier   = 0.05f,
        MaxMultiplier   = 10.0f,

        // Applied when the healer has none of the traits below: a flat +25% across the board.
        Defaults = new HealingValues
        {
            HpMultiplier         = 1.25f,
            HealSpeedMultiplier  = 1.25f,
            ApplySpeedMultiplier = 1.25f
        },

        TraitModifiers =
        [
            // ── Positive traits ──────────────────────────────────────────────

            new TraitModifierConfig
            {
                TraitCode = "soldier",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.25f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 1.55f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "hardy",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.45f,
                    HealSpeedMultiplier  = 1.35f,
                    ApplySpeedMultiplier = 1.00f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "mender",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.25f,
                    HealSpeedMultiplier  = 1.25f,
                    ApplySpeedMultiplier = 1.65f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "furtive",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.00f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 1.45f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "resourceful",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.55f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 1.00f
                }
            },

            // ── Negative traits ──────────────────────────────────────────────

            new TraitModifierConfig
            {
                TraitCode = "frail",
                Values = new HealingValues
                {
                    HpMultiplier         = 0.65f,
                    HealSpeedMultiplier  = 0.80f,
                    ApplySpeedMultiplier = 1.00f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "nervous",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.00f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 0.55f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "weak",
                Values = new HealingValues
                {
                    HpMultiplier         = 0.70f,
                    HealSpeedMultiplier  = 0.70f,
                    ApplySpeedMultiplier = 1.00f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "kind",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.00f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 0.65f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "ravenous",
                Values = new HealingValues
                {
                    HpMultiplier         = 0.80f,
                    HealSpeedMultiplier  = 0.90f,
                    ApplySpeedMultiplier = 0.80f
                }
            }
        ]
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// HealingValues — the three per-parameter multipliers
//
// A multiplier of 1.0 means "no change". Above 1.0 is an improvement, below 1.0 is
// a penalty. The three multipliers map onto the three stages of a heal:
//
//   HpMultiplier         → how much health the item ultimately restores
//   HealSpeedMultiplier  → how fast the restoration ticks resolve once applied
//   ApplySpeedMultiplier → how fast the item can be applied (cast time)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The three multipliers describing how a heal is modified. Used both for
/// <see cref="HealingHandsConfig.Defaults"/> and for each
/// <see cref="TraitModifierConfig.Values"/>.
/// </summary>
public class HealingValues
{
    /// <summary>
    /// Scales the total HP the item restores. 1.0 leaves it unchanged; 1.25 restores 25%
    /// more; 0.70 restores 30% less. Applied to the heal as it lands on the target by
    /// <see cref="HealingHands.Systems.HealReceiveBehavior"/>.
    /// </summary>
    public float HpMultiplier { get; set; } = 1.0f;

    /// <summary>
    /// Scales how fast the heal-over-time ticks resolve. Above 1.0 compresses the same total
    /// HP into a shorter window so the heal finishes sooner; below 1.0 stretches it out. The
    /// total HP restored is unchanged — only the pacing. Applied by
    /// <see cref="HealingHands.Systems.HealReceiveBehavior"/>.
    /// </summary>
    public float HealSpeedMultiplier { get; set; } = 1.0f;

    /// <summary>
    /// Scales how fast the item is applied — the cast/charge time before the heal fires.
    /// Above 1.0 shortens the cast; below 1.0 lengthens it. This is realized through the
    /// vanilla <c>healingeffectivness</c> stat by
    /// <see cref="HealingHands.Systems.HealingInterceptBehavior"/>, so the contribution
    /// stacks on top of whatever that stat is already at from the healer's gear, food, etc.
    /// </summary>
    public float ApplySpeedMultiplier { get; set; } = 1.0f;
}

// ─────────────────────────────────────────────────────────────────────────────
// CompoundingMode — how several matching traits combine
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The three ways multiple matching trait multipliers can be combined. See
/// <see cref="HealingHandsConfig.CompoundingMode"/> for the exact formulas and trade-offs.
/// </summary>
public enum CompoundingMode
{
    /// <summary>Chain the multipliers: m1 × m2 × … × mN. Bonuses compound on each other.</summary>
    Multiplicative,

    /// <summary>Sum the deltas from 1.0: 1 + (m1−1) + … + (mN−1). Linear; bonuses and penalties cancel.</summary>
    Additive,

    /// <summary>Take only the single largest multiplier per parameter. No stacking.</summary>
    Highest
}

// ─────────────────────────────────────────────────────────────────────────────
// TraitModifierConfig — one trait's contribution
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Binds one character trait to the multipliers it contributes when the healer has it.
/// </summary>
public class TraitModifierConfig
{
    /// <summary>
    /// The trait code to look for in the healer's <c>WatchedAttributes["traits"]</c> tree.
    /// Vanilla codes are listed in <c>assets/game/config/characterclasses.json</c>; modded
    /// traits use the same convention and work identically.
    /// </summary>
    public string TraitCode { get; set; } = "";


    /// <summary>The multipliers this trait contributes when matched.</summary>
    public HealingValues Values { get; set; } = new();
}

// ─────────────────────────────────────────────────────────────────────────────
// HealModifier — the resolved multipliers for a single heal event
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The final, combined, clamped multipliers for one heal event. Produced by
/// <see cref="Compute"/> and consumed by
/// <see cref="HealingHands.Systems.HealingInterceptBehavior"/> (application speed) and
/// <see cref="HealingHands.Systems.HealReceiveBehavior"/> (HP and heal-over-time speed).
/// </summary>
public readonly struct HealModifier
{
    public float HpMultiplier         { get; init; }
    public float HealSpeedMultiplier  { get; init; }
    public float ApplySpeedMultiplier { get; init; }

    /// <summary>True when all three multipliers are exactly 1.0 — i.e. nothing to apply.</summary>
    internal bool IsIdentity =>
        HpMultiplier         == 1.0f &&
        HealSpeedMultiplier  == 1.0f &&
        ApplySpeedMultiplier == 1.0f;

    /// <summary>A no-op modifier, returned when the mod is disabled or the heal is a self-heal.</summary>
    internal static readonly HealModifier Identity = new()
    {
        HpMultiplier = 1.0f, HealSpeedMultiplier = 1.0f, ApplySpeedMultiplier = 1.0f
    };

    /// <summary>
    /// Resolves the combined modifier for <paramref name="healer"/>:
    /// <list type="bullet">
    ///   <item>No configured trait matches → returns the clamped
    ///   <see cref="HealingHandsConfig.Defaults"/>.</item>
    ///   <item>One or more traits match → combines only the matched trait values using
    ///   <see cref="HealingHandsConfig.CompoundingMode"/>, then clamps. Defaults are not used.</item>
    /// </list>
    /// Matching reads <c>WatchedAttributes["traits"]</c> as a flat tree of boolean entries
    /// keyed by trait code — the convention used by both vanilla and modded traits.
    /// </summary>
    internal static HealModifier Compute(
        Entity healer,
        HealingHandsConfig config,
        ILogger? logger,
        string healerName)
    {
        var traitsTree = healer.WatchedAttributes.GetTreeAttribute("traits");

        // Gather every configured trait the healer actually has.
        List<HealingValues> matched = [];
        foreach (TraitModifierConfig entry in config.TraitModifiers)
        {
            if (string.IsNullOrWhiteSpace(entry.TraitCode)) continue;
            if (traitsTree?.GetBool(entry.TraitCode, false) ?? false)
            {
                matched.Add(entry.Values);
                logger?.Debug(
                    $"[HealingHands] {healerName}: trait '{entry.TraitCode}' matched " +
                    $"(hp×{entry.Values.HpMultiplier:F2}, " +
                    $"speed×{entry.Values.HealSpeedMultiplier:F2}, " +
                    $"apply×{entry.Values.ApplySpeedMultiplier:F2})");
            }
        }

        // No trait matched → use the defaults block.
        if (matched.Count == 0)
        {
            HealingValues d = config.Defaults;
            return new HealModifier
            {
                HpMultiplier         = Math.Clamp(d.HpMultiplier,         config.MinMultiplier, config.MaxMultiplier),
                HealSpeedMultiplier  = Math.Clamp(d.HealSpeedMultiplier,  config.MinMultiplier, config.MaxMultiplier),
                ApplySpeedMultiplier = Math.Clamp(d.ApplySpeedMultiplier, config.MinMultiplier, config.MaxMultiplier)
            };
        }

        // One or more traits matched → compound them; defaults are ignored.
        float hp    = Combine(matched, v => v.HpMultiplier,         config.CompoundingMode);
        float speed = Combine(matched, v => v.HealSpeedMultiplier,  config.CompoundingMode);
        float apply = Combine(matched, v => v.ApplySpeedMultiplier, config.CompoundingMode);

        return new HealModifier
        {
            HpMultiplier         = Math.Clamp(hp,    config.MinMultiplier, config.MaxMultiplier),
            HealSpeedMultiplier  = Math.Clamp(speed, config.MinMultiplier, config.MaxMultiplier),
            ApplySpeedMultiplier = Math.Clamp(apply, config.MinMultiplier, config.MaxMultiplier)
        };
    }

    /// <summary>
    /// Combines one parameter across all matched traits according to <paramref name="mode"/>.
    /// See <see cref="HealingHandsConfig.CompoundingMode"/> for what each mode means.
    /// </summary>
    private static float Combine(
        List<HealingValues> entries,
        System.Func<HealingValues, float> sel,
        CompoundingMode mode) => mode switch
    {
        CompoundingMode.Multiplicative => entries.Aggregate(1.0f, (acc, e) => acc * sel(e)),
        CompoundingMode.Additive       => 1.0f + entries.Sum(e => sel(e) - 1.0f),
        CompoundingMode.Highest        => entries.Max(sel),
        _                              => 1.0f
    };
}
