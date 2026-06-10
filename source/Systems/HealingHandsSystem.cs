using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace HealingHands.Systems;

/// <summary>
/// Owns modifier computation and provides the <see cref="Func{T1,T2,TResult}"/> delegate
/// that both <see cref="HealReceiveBehavior"/> and <see cref="HealingInterceptBehavior"/> call.
/// Swapped out atomically on <c>/healinghands reload</c>.
/// </summary>
public sealed class HealingHandsSystem
{
    private readonly ICoreServerAPI _api;

    // Config reference is read every heal; updated atomically on reload by replacing the
    // whole HealingHandsSystem instance in HealingHandsModSystem.
    public HealingHandsConfig Config { get; private set; }

    public HealingHandsSystem(ICoreServerAPI api, HealingHandsConfig config)
    {
        _api   = api;
        Config = config;
    }

    /// <summary>
    /// Computes and returns the combined <see cref="HealModifier"/> for the given
    /// healer→target pair. Returns <see cref="HealModifier.Identity"/> when the mod
    /// is disabled or the interaction is filtered by
    /// <see cref="HealingHandsConfig.OnlyAffectOtherPlayerHeals"/>.
    /// </summary>
    public HealModifier ComputeModifier(Entity healer, Entity target)
    {
        if (!Config.Enabled) return HealModifier.Identity;

        if (Config.OnlyAffectOtherPlayerHeals)
        {
            // Skip self-heals and heals on non-players.
            if (healer.EntityId == target.EntityId) return HealModifier.Identity;
            if (target is not EntityPlayer)          return HealModifier.Identity;
        }

        string healerName =
            healer.GetName()
            ?? healer.EntityId.ToString();

        return HealModifier.Compute(healer, Config, _api.Logger, healerName);
    }
}
