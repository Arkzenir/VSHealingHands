using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace HealingHands;

// ─────────────────────────────────────────────────────────────────────────────
// Top-level config
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Root configuration for HealingHands. Loaded from ModConfig/healinghands.json.
/// </summary>
public class HealingHandsConfig
{
    /// <summary>
    /// Master switch. When false the mod is fully inert.
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, trait modifiers only fire when the healer targets a
    /// <em>different</em> player (Ctrl + aim + use). Self-heals pass through
    /// unmodified. When false self-heals are also affected.
    /// Default: true.
    /// </summary>
    public bool OnlyAffectOtherPlayerHeals { get; set; } = true;

    /// <summary>
    /// Multipliers used when the healer has <em>none</em> of the listed traits.
    /// If the healer has at least one matching trait these defaults are ignored
    /// entirely; only the compounded trait values apply.
    /// </summary>
    public HealingValues Defaults { get; set; } = new();

    /// <summary>
    /// Per-trait entries. Each entry declares how a single trait shifts the
    /// three healing parameters. When a healer has more than one listed trait
    /// all matching entries are combined via <see cref="CompoundingMode"/>.
    /// Any trait code — vanilla or modded — is valid here; unrecognised codes
    /// are simply never matched and have no effect.
    /// </summary>
    public List<TraitModifierConfig> TraitModifiers { get; set; } = [];

    /// <summary>
    /// Controls how multiple matching trait multipliers are combined.
    /// <list type="bullet">
    ///   <item><term>Multiplicative</term><description>m1 × m2 × … × mN</description></item>
    ///   <item><term>Additive</term><description>1 + (m1−1) + (m2−1) + … — linear, avoids runaway</description></item>
    ///   <item><term>Highest</term><description>Only the single highest per parameter</description></item>
    /// </list>
    /// Default: Multiplicative.
    /// </summary>
    public CompoundingMode CompoundingMode { get; set; } = CompoundingMode.Additive;

    /// <summary>Minimum allowed final multiplier. Default: 0.05.</summary>
    public float MinMultiplier { get; set; } = 0.05f;

    /// <summary>Maximum allowed final multiplier. Default: 10.0.</summary>
    public float MaxMultiplier { get; set; } = 10.0f;

    // ── Default config ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the default config written on first run.
    /// All trait codes here are from the vanilla traits.json
    /// (assets/game/config/characterclasses.json).
    /// </summary>
    public static HealingHandsConfig CreateDefault() => new()
    {
        Enabled                    = true,
        OnlyAffectOtherPlayerHeals = true,
        CompoundingMode            = CompoundingMode.Additive,
        MinMultiplier              = 0.05f,
        MaxMultiplier              = 10.0f,

        // Defaults: applied when the healer has NONE of the listed traits.
        // 1.0 on all multipliers means the item's authored values are used as-is.
        // Outline: "Everyone applies bandages 10% better on others, compared to themselves."
        // These defaults apply when the healer has NONE of the configured traits.
        Defaults = new HealingValues
        {
            HpMultiplier         = 1.10f,
            HealSpeedMultiplier  = 1.10f,
            ApplySpeedMultiplier = 1.10f
        },

        TraitModifiers =
        [
            // ── Positive vanilla traits (healing-relevant) ───────────────────

            new TraitModifierConfig
            {
                TraitCode = "soldier",
                Comment   = "Vanilla positive trait. Hardened fighter — faster application and " +
                            "slightly more durable heals (knows how to work under pressure).",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.10f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 1.25f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "hardy",
                Comment   = "Vanilla positive trait. Robust constitution — heals others " +
                            "for more and the heal resolves faster.",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.20f,
                    HealSpeedMultiplier  = 1.15f,
                    ApplySpeedMultiplier = 1.00f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "mender",
                Comment   = "Vanilla positive trait. Skilled at repair and maintenance — " +
                            "naturally adept at applying remedies quickly.",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.10f,
                    HealSpeedMultiplier  = 1.10f,
                    ApplySpeedMultiplier = 1.30f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "merciless",
                Comment   = "Vanilla positive trait. Precise and efficient — applies healing " +
                            "items with clinical speed.",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.00f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 1.40f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "resourceful",
                Comment   = "Vanilla positive trait. Gets more out of everything — " +
                            "including healing items.",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.25f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 1.00f
                }
            },

            // ── Negative vanilla traits (healing penalty) ────────────────────

            new TraitModifierConfig
            {
                TraitCode = "frail",
                Comment   = "Vanilla negative trait. Weak constitution — has less energy " +
                            "to channel into healing others.",
                Values = new HealingValues
                {
                    HpMultiplier         = 0.80f,
                    HealSpeedMultiplier  = 0.90f,
                    ApplySpeedMultiplier = 1.00f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "nervous",
                Comment   = "Vanilla negative trait. Anxious under pressure — slower to " +
                            "complete the application.",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.00f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 0.70f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "weak",
                Comment   = "Vanilla negative trait. Physically underpowered — less effective " +
                            "at forcing remedies to take hold.",
                Values = new HealingValues
                {
                    HpMultiplier         = 0.85f,
                    HealSpeedMultiplier  = 0.85f,
                    ApplySpeedMultiplier = 1.00f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "kind",
                Comment   = "Vanilla negative trait. Too gentle — takes longer to apply " +
                            "healing items (careful but slow).",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.00f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 0.80f
                }
            },
            new TraitModifierConfig
            {
                TraitCode = "furtive",
                Comment   = "Vanilla positive trait. Light-footed and precise — quick hands " +
                            "translate to faster item application.",
                Values = new HealingValues
                {
                    HpMultiplier         = 1.00f,
                    HealSpeedMultiplier  = 1.00f,
                    ApplySpeedMultiplier = 1.20f
                }
            }
        ]
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// HealingValues
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The three multipliers for a single heal context. Used for both
/// <see cref="HealingHandsConfig.Defaults"/> and <see cref="TraitModifierConfig.Values"/>.
/// </summary>
public class HealingValues
{
    /// <summary>
    /// Multiplier on the item's authored HP amount.
    /// 1.0 = unchanged; 1.3 = 30 % extra; 0.8 = 20 % less.
    /// <para>Note: stacks multiplicatively with the healer's vanilla
    /// <c>healingeffectivness</c> stat (set by character class bonuses,
    /// gear, potions, etc.).</para>
    /// </summary>
    public float HpMultiplier { get; set; } = 1.0f;

    /// <summary>
    /// Multiplier on how fast heal-over-time ticks resolve.
    /// Values above 1.0 shorten the HoT duration (ticks arrive faster).
    /// </summary>
    public float HealSpeedMultiplier { get; set; } = 1.0f;

    /// <summary>
    /// Multiplier on the item's use/cast duration.
    /// Values above 1.0 shorten the cast time; values below 1.0 lengthen it.
    /// <para>Note: stacks multiplicatively with the healer's vanilla
    /// <c>healingeffectivness</c> stat.</para>
    /// </summary>
    public float ApplySpeedMultiplier { get; set; } = 1.0f;
}

// ─────────────────────────────────────────────────────────────────────────────
// CompoundingMode
// ─────────────────────────────────────────────────────────────────────────────

public enum CompoundingMode { Multiplicative, Additive, Highest }

// ─────────────────────────────────────────────────────────────────────────────
// TraitModifierConfig
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Defines how one character trait modifies healing applied to another player.
/// </summary>
public class TraitModifierConfig
{
    /// <summary>
    /// Exact trait code to match in the healer's
    /// <c>entity.WatchedAttributes["traits"]</c> tree.
    /// Vanilla codes are listed in <c>assets/game/config/characterclasses.json</c>
    /// (or <c>traits.json</c> in the mod's config folder). Modded trait codes work
    /// identically — any string that matches a key in that attribute tree is valid.
    /// </summary>
    public string TraitCode { get; set; } = "";

    /// <summary>Optional human-readable note. Not used for logic.</summary>
    public string Comment { get; set; } = "";

    /// <summary>The multipliers this trait contributes.</summary>
    public HealingValues Values { get; set; } = new();
}

// ─────────────────────────────────────────────────────────────────────────────
// HealModifier — computed once per heal event
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Resolved combined multipliers for a single heal event.
/// </summary>
public readonly struct HealModifier
{
    public float HpMultiplier         { get; init; }
    public float HealSpeedMultiplier  { get; init; }
    public float ApplySpeedMultiplier { get; init; }

    internal bool IsIdentity =>
        HpMultiplier         == 1.0f &&
        HealSpeedMultiplier  == 1.0f &&
        ApplySpeedMultiplier == 1.0f;

    internal static readonly HealModifier Identity = new()
    {
        HpMultiplier = 1.0f, HealSpeedMultiplier = 1.0f, ApplySpeedMultiplier = 1.0f
    };

    /// <summary>
    /// Computes the combined modifier for <paramref name="healer"/>.
    /// <list type="bullet">
    ///   <item>No matching traits → returns <see cref="HealingHandsConfig.Defaults"/>.</item>
    ///   <item>At least one matching trait → compounds only trait values; defaults ignored.</item>
    /// </list>
    /// Trait matching reads <c>entity.WatchedAttributes["traits"]</c> as a flat
    /// <see cref="ITreeAttribute"/> of boolean values keyed by trait code. This works
    /// for both vanilla traits and any modded trait that follows the same convention.
    /// </summary>
    internal static HealModifier Compute(
        Entity healer,
        HealingHandsConfig config,
        ILogger? logger,
        string healerName)
    {
        var traitsTree = healer.WatchedAttributes.GetTreeAttribute("traits");

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

        // No traits matched → use defaults.
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

        // At least one trait matched → compound; defaults are NOT used.
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
