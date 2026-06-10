using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace HealingHands.Systems;

/// <summary>
/// Per-player <see cref="EntityBehavior"/> added to every online player entity.
/// Intercepts incoming heal events to scale HP amount and heal-over-time speed
/// based on the modifier established by <see cref="HealingInterceptBehavior"/>.
///
/// <para><b>No shared state is mutated.</b> Each player entity owns exactly one instance.
/// <c>ref float damage</c> and <c>damageSource.Duration</c> are the only things written,
/// both on objects that are entirely local to the current <c>ReceiveDamage</c> call stack.</para>
///
/// <para><b>Context handoff:</b><br/>
/// <c>BehaviorHealingItem</c> constructs its <c>DamageSource</c> with
/// <c>Source = EnumDamageSource.Internal</c> and no <c>SourceEntity</c>.
/// <see cref="HealingInterceptBehavior.OnHeldInteractStop"/> calls
/// <see cref="SetPending"/> on this behavior immediately before vanilla's Stop runs
/// <c>ReceiveDamage</c>. Because the entire chain is synchronous on the main server thread,
/// the pending value is guaranteed to be present when <see cref="OnEntityReceiveDamage"/>
/// fires, and is consumed and cleared in a single read.</para>
///
/// <para><b>Why a simple nullable field rather than a dictionary:</b><br/>
/// A dictionary keyed by healer entity ID would require "take the first entry" logic to
/// consume the pending modifier, because <c>DamageSource.SourceEntity</c> is null and we
/// cannot match a healer to an entry. "Take the first entry" is only correct when there is
/// exactly one entry — which is always true because the entire
/// <c>HealingInterceptBehavior.Stop → BehaviorHealingItem.Stop → ReceiveDamage →
/// OnEntityReceiveDamage</c> chain executes synchronously before any other heal can begin.
/// A nullable field makes this invariant explicit in the type: there is either one pending
/// modifier or none. It is simpler, has no iteration overhead, and carries no ambiguity.</para>
///
/// <para><b>Scenario: Player A self-heals.</b><br/>
/// <see cref="HealingInterceptBehavior"/> returns from <c>Start</c> and <c>Stop</c> before
/// calling <see cref="SetPending"/>. <c>_pending</c> is null. <c>OnEntityReceiveDamage</c>
/// checks <c>_pending.HasValue</c> and returns immediately.</para>
///
/// <para><b>Scenario: Player C is simultaneously healed by Player D using the same item type.</b><br/>
/// Player C's <see cref="HealReceiveBehavior"/> is a completely separate instance from Player A's —
/// each entity owns its own. Player D's modifier is installed in C's instance; Player B's
/// modifier is installed in A's instance. The two call stacks are sequential (main thread) and
/// operate on entirely different objects. No interference is possible.</para>
/// </summary>
public sealed class HealReceiveBehavior : EntityBehavior
{
    public override string PropertyName() => "healinghands:healreceive";

    // Set by HealingInterceptBehavior.Stop immediately before vanilla calls ReceiveDamage.
    // Consumed and nulled in OnEntityReceiveDamage.
    // Null means no cross-player heal is in flight for this entity.
    private HealModifier? _pending;

    public HealReceiveBehavior(Entity entity) : base(entity) { }

    // ── Context injection (called by HealingInterceptBehavior) ────────────────

    /// <summary>
    /// Registers the modifier to apply for the immediately upcoming heal.
    /// Must be called on the main server thread immediately before
    /// <c>BehaviorHealingItem.OnHeldInteractStop</c> triggers <c>ReceiveDamage</c>.
    /// </summary>
    internal void SetPending(HealModifier modifier) => _pending = modifier;

    /// <summary>
    /// Clears the pending modifier without applying it.
    /// Called when a heal is cancelled before it completes.
    /// </summary>
    internal void ClearPending() => _pending = null;

    // ── Intercept ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="Entity.ReceiveDamage"/> before <c>EntityBehaviorHealth</c>
    /// applies the value to HP. We modify <c>ref damage</c> and <c>damageSource.Duration</c>
    /// in-place when a pending modifier has been registered.
    /// </summary>
    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        // Fast exits — no pending modifier means nothing to do.
        // These cover: self-heals (SetPending was never called), regen ticks,
        // potions drunk by the player themselves, and any non-heal damage.
        if (!_pending.HasValue) return;
        if (damageSource.Type   != EnumDamageType.Heal)           return;
        if (damageSource.Source != EnumDamageSource.Internal)     return;

        // Consume the pending modifier. After this point _pending is null,
        // so any further ReceiveDamage calls in this tick are unaffected.
        HealModifier mod = _pending.Value;
        _pending = null;

        // ── HP multiplier ──────────────────────────────────────────────────────
        // Scales the raw heal amount before EntityBehaviorHealth writes it to HP.
        if (mod.HpMultiplier != 1.0f)
            damage = MathF.Max(0f, damage * mod.HpMultiplier);

        // ── Heal-over-time speed ───────────────────────────────────────────────
        // BehaviorHealingItem sets Duration = EffectDurationSec and TicksPerDuration = Ticks.
        // EntityBehaviorHealth fires ticks at interval = Duration / TicksPerDuration.
        // Dividing Duration by HealSpeedMultiplier makes ticks arrive faster while
        // preserving the total HP healed. DamageSource is constructed fresh per call —
        // safe to mutate.
        if (mod.HealSpeedMultiplier != 1.0f && damageSource.Duration > TimeSpan.Zero)
        {
            double newSec = damageSource.Duration.TotalSeconds / mod.HealSpeedMultiplier;
            damageSource.Duration = TimeSpan.FromSeconds(Math.Max(0.05, newSec));
        }
    }
}
