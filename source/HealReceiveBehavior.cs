using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VSHealingHands;

/// <summary>
/// Carried by every online player. Applies the HP-amount and healing-speed part of a
/// pending modifier to the next heal that lands on this player - handed to it by
/// <see cref="HealingInterceptBehavior"/> on the same synchronous call stack,
/// immediately before vanilla turns the heal into a damage-over-time effect.
/// <br/>
/// State lives entirely on this entity: two players being healed at the same time
/// never touch each other's data (each has their own instance of this behavior), and a
/// pending modifier is consumed exactly once, right here, regardless of how many
/// threads the server happens to process interactions on.
/// </summary>
public class HealReceiveBehavior : EntityBehavior
{
    public override string PropertyName() => "vshealinghands:healreceive";

    HealingValues? pending;

    public HealReceiveBehavior(Entity entity) : base(entity) { }

    internal void SetPending(HealingValues values) => pending = values;
    internal void ClearPending() => pending = null;

    /// <summary>
    /// Vanilla's CollectibleBehaviorHealingItem.OnHeldInteractStop builds a Heal-type
    /// DamageSource from the item's own (untouched) Health/EffectDurationSec/Ticks and
    /// calls Entity.ReceiveDamage, which runs every EntityBehavior's
    /// OnEntityReceiveDamage in list order. EntityBehaviorHealth - registered from the
    /// entity's own JSON, so already in the list before this behavior is ever added -
    /// turns a Heal-type damage with Duration > 0 into a damage-over-time effect using
    /// whatever damage/Duration it sees when its turn comes up. For our scaling to have
    /// any effect, this behavior's turn must come up first, which is why
    /// VSHealingHandsModSystem inserts it at index 0 instead of appending it.
    /// </summary>
    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        if (pending == null) return;
        if (damageSource.Type != EnumDamageType.Heal) return;
        if (damageSource.Source != EnumDamageSource.Internal) return;

        HealingValues values = pending;
        pending = null;

        if (values.HealAmountMultiplier != 1f)
        {
            damage = MathF.Max(0f, damage * values.HealAmountMultiplier);
        }

        if (values.HealingSpeedMultiplier > 0f && values.HealingSpeedMultiplier != 1f && damageSource.Duration > TimeSpan.Zero)
        {
            double newSeconds = damageSource.Duration.TotalSeconds / values.HealingSpeedMultiplier;
            damageSource.Duration = TimeSpan.FromSeconds(Math.Max(0.05, newSeconds));
        }
    }
}
