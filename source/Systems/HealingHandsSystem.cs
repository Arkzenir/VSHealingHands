using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace HealingHands.Systems;

/// <summary>
/// Holds the active config and turns a (healer, target) pair into the <see cref="HealModifier"/>
/// that should apply to their heal. Both <see cref="HealingInterceptBehavior"/> and the
/// <c>/healinghands checktraits</c> command resolve modifiers through here.
/// <para>On <c>/healinghands reload</c> the mod system replaces this whole instance, so callers
/// always read fresh config without any per-item re-injection.</para>
/// </summary>
public sealed class HealingHandsSystem
{
    private readonly ICoreServerAPI _api;

    /// <summary>The config this system was built with.</summary>
    public HealingHandsConfig Config { get; private set; }

    public HealingHandsSystem(ICoreServerAPI api, HealingHandsConfig config)
    {
        _api   = api;
        Config = config;
    }

    /// <summary>
    /// Resolves the combined modifier for <paramref name="healer"/> healing
    /// <paramref name="target"/>. Returns <see cref="HealModifier.Identity"/> (no change) when
    /// the mod is disabled, or when the heal is a self-heal — the mod only ever modifies heals
    /// applied to another player. (Non-player targets are filtered earlier, in
    /// <see cref="HealingInterceptBehavior"/>.)
    /// </summary>
    public HealModifier ComputeModifier(Entity healer, Entity target)
    {
        if (!Config.Enabled) return HealModifier.Identity;
        if (healer.EntityId == target.EntityId) return HealModifier.Identity;

        string healerName = healer.GetName() ?? healer.EntityId.ToString();
        return HealModifier.Compute(healer, Config, _api.Logger, healerName);
    }
}
