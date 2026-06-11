using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace HealingHands.Systems;

/// <summary>
/// An <see cref="EntityBehavior"/> carried by every online player. It applies the HP and
/// heal-over-time-speed parts of a modifier to heals that land on this player.
///
/// <para><b>How the modifier arrives.</b> Vanilla's healing item builds its
/// <c>DamageSource</c> with <c>Source = EnumDamageSource.Internal</c> and no
/// <c>SourceEntity</c>, so the heal carries no reference to who cast it. Instead,
/// <see cref="HealingInterceptBehavior.OnHeldInteractStop"/> calls <see cref="SetPending"/>
/// on this behavior just before vanilla applies the heal. Everything runs synchronously on
/// the main server thread, so the pending modifier is guaranteed to be present when
/// <see cref="OnEntityReceiveDamage"/> fires and is consumed in a single read.</para>
///
/// <para><b>Why a single nullable field.</b> Because the heal carries no healer reference,
/// there's no key to look a modifier up by — we simply rely on the fact that the
/// set-then-apply sequence is atomic on one thread, so at most one modifier is ever pending.
/// A nullable field expresses exactly that: either one modifier is pending or none is.</para>
///
/// <para><b>Isolation.</b> Each player owns a separate instance, so two simultaneous heals on
/// two different targets never interact: each healer's <see cref="HealingInterceptBehavior"/>
/// sets the pending modifier on its own target's behavior.</para>
///
/// <para>No shared state is mutated: the only writes are to <c>ref damage</c> and
/// <c>damageSource.Duration</c>, both local to the current <c>ReceiveDamage</c> call.</para>
/// </summary>
public sealed class HealReceiveBehavior : EntityBehavior
{
    public override string PropertyName() => "healinghands:healreceive";

    // The modifier to apply to the next heal that lands on this player, or null if none is
    // pending. Set by HealingInterceptBehavior.Stop, consumed (and cleared) in
    // OnEntityReceiveDamage.
    private HealModifier? _pending;

    public HealReceiveBehavior(Entity entity) : base(entity) { }

    // ── Context handoff (called by HealingInterceptBehavior) ──────────────────

    /// <summary>
    /// Registers the modifier for the heal that is about to be applied to this player.
    /// Called on the main server thread immediately before vanilla triggers <c>ReceiveDamage</c>.
    /// </summary>
    internal void SetPending(HealModifier modifier) => _pending = modifier;

    /// <summary>Drops a pending modifier without applying it (used when a cast is cancelled).</summary>
    internal void ClearPending() => _pending = null;

    // ── Intercept ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs for every damage/heal event on this entity, before the health system writes the
    /// value to HP. When a modifier is pending, scales the HP amount and the heal-over-time
    /// duration on the local <c>DamageSource</c>.
    /// </summary>
    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        // Nothing pending → ordinary heal/damage, leave it alone. This is the path taken by
        // self-heals (SetPending was never called), natural regen, drunk potions, and all
        // non-heal damage.
        if (!_pending.HasValue) return;
        if (damageSource.Type   != EnumDamageType.Heal)       return;
        if (damageSource.Source != EnumDamageSource.Internal) return;

        // Consume the modifier so it applies to exactly one heal.
        HealModifier mod = _pending.Value;
        _pending = null;

        // HP amount: scale the heal before the health system applies it.
        if (mod.HpMultiplier != 1.0f)
            damage = MathF.Max(0f, damage * mod.HpMultiplier);

        // Heal-over-time speed: vanilla sets Duration = EffectDurationSec and
        // TicksPerDuration = Ticks, and the health system spreads ticks across Duration.
        // Dividing Duration compresses the same total heal into a shorter window (faster
        // ticks). The DamageSource is freshly built for this call, so mutating it is safe.
        if (mod.HealSpeedMultiplier != 1.0f && damageSource.Duration > TimeSpan.Zero)
        {
            double newSec = damageSource.Duration.TotalSeconds / mod.HealSpeedMultiplier;
            damageSource.Duration = TimeSpan.FromSeconds(Math.Max(0.05, newSec));
        }
    }
}
