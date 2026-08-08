using System.Collections.Generic;

namespace VSHealingHands;

public class HealingValues
{
    public float ApplicationSpeedMultiplier { get; set; } = 1f;
    public float HealingSpeedMultiplier { get; set; } = 1f;
    public float HealAmountMultiplier { get; set; } = 1f;
}

public class HealingHandsConfig
{
    public HealingValues Defaults { get; set; } = new();

    public Dictionary<string, HealingValues> Traits { get; set; } = new()
    {
        ["mender"] = new HealingValues
        {
            ApplicationSpeedMultiplier = 1.5f,
            HealingSpeedMultiplier = 1.25f,
            HealAmountMultiplier = 1.25f
        }
    };

    /// <summary>
    /// Resolves the values that apply for the given healer: the traits present in
    /// this config that the healer has, compounded additively (each trait's delta
    /// from the neutral 1.0 multiplier is summed on top of 1.0); or the defaults if
    /// the healer has none of the configured traits.
    /// </summary>
    public HealingValues Resolve(IEnumerable<string> presentTraitCodes)
    {
        bool any = false;
        float applicationSpeedDelta = 0f;
        float healingSpeedDelta = 0f;
        float healAmountDelta = 0f;

        foreach (string traitCode in presentTraitCodes)
        {
            if (!Traits.TryGetValue(traitCode, out HealingValues? traitValues)) continue;

            any = true;
            applicationSpeedDelta += traitValues.ApplicationSpeedMultiplier - 1f;
            healingSpeedDelta += traitValues.HealingSpeedMultiplier - 1f;
            healAmountDelta += traitValues.HealAmountMultiplier - 1f;
        }

        if (!any) return Defaults;

        return new HealingValues
        {
            ApplicationSpeedMultiplier = 1f + applicationSpeedDelta,
            HealingSpeedMultiplier = 1f + healingSpeedDelta,
            HealAmountMultiplier = 1f + healAmountDelta
        };
    }
}
