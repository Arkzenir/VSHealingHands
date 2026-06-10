using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace HealingHands.Systems;

/// <summary>
/// <see cref="CollectibleBehavior"/> prepended at index 0 into every healing item.
/// Handles all three healing parameters without mutating the shared
/// <see cref="BehaviorHealingItem.Config"/> object.
///
/// <para><b>Why prepend?</b><br/>
/// <c>BehaviorHealingItem.OnHeldInteractStart</c> sets
/// <c>handling = EnumHandling.PreventSubsequent</c>, aborting the behavior loop immediately.
/// Any behavior appended after <c>BehaviorHealingItem</c> never has its <c>Start</c> called.
/// Prepending ensures our <c>Start</c> fires before <c>PreventSubsequent</c> is set.</para>
///
/// <para><b>ApplySpeed — <c>healingeffectivness</c> stat</b><br/>
/// <c>BehaviorHealingItem.GetApplicationTime</c> reads
/// <c>entity.Stats.GetBlended("healingeffectivness")</c> live on every Step tick and again
/// in Stop's completion guard. We write a trait-derived delta to that stat in <c>Start</c>
/// so vanilla's formula produces the modified cast duration automatically, including its
/// asymmetric penalty curve toward <c>MaxApplicationTimeSec</c>. The stat is removed in a
/// 0 ms <c>RegisterCallback</c> fired from <c>Stop</c>/<c>Cancel</c>, which defers cleanup
/// to the next game tick — after vanilla's Stop has read the stat for its guard check.</para>
///
/// <para><b>HP + HoT speed</b><br/>
/// These are set at <c>Stop</c> time, not <c>Start</c>, because the final target is
/// only confirmed when vanilla calls <c>GetTargetEntity</c> inside its own <c>Stop</c>.
/// We mirror that resolution, re-compute the modifier, and call
/// <see cref="HealReceiveBehavior.SetPending"/> on the target's behavior immediately before
/// vanilla's <c>Stop</c> runs. Vanilla then calls <c>ReceiveDamage</c> synchronously,
/// which triggers <see cref="HealReceiveBehavior.OnEntityReceiveDamage"/> where the modifier
/// is consumed.</para>
///
/// <para><b>Self-heal: complete no-op.</b><br/>
/// <see cref="ShouldSkip"/> detects that <c>target == healer</c> (or target is not a player
/// when the config restricts to player-to-player heals) and returns from both <c>Start</c>
/// and <c>Stop</c> without touching any state. No stat is set, no context is installed,
/// no callback is scheduled.</para>
///
/// <para><b>Isolation across concurrent uses of the same item type.</b><br/>
/// This behavior is one instance per item type (shared across all users of e.g. "linen bandage").
/// State is stored in <c>_statSetForHealer</c>, a <c>HashSet&lt;long&gt;</c> keyed by healer
/// entity ID. Two players using the same item type add their own distinct IDs; their
/// cleanup callbacks remove only their own ID. <see cref="HealReceiveBehavior"/> instances
/// are per-target-entity and hold a single <c>HealModifier?</c> field — Player A's and
/// Player C's instances are entirely separate objects.</para>
/// </summary>
public sealed class HealingInterceptBehavior : CollectibleBehavior
{
    private readonly System.Func<Entity, Entity, HealModifier>? _computeModifier;
    private readonly ILogger? _logger;

    // Tracks which healer entity IDs have had their healingeffectivness stat modified
    // by us during the current cast, so we only schedule removal for stats we actually set.
    // One entry per actively-casting healer; removed when their Stop/Cancel fires.
    private readonly HashSet<long> _statSetForHealer = new();

    // ── Registry constructor (client + deserialization) ───────────────────────

    public HealingInterceptBehavior(CollectibleObject collObj) : base(collObj) { }

    // ── Injection constructor ─────────────────────────────────────────────────

    internal HealingInterceptBehavior(
        CollectibleObject collObj,
        System.Func<Entity, Entity, HealModifier> computeModifier,
        ILogger logger)
        : base(collObj)
    {
        _computeModifier = computeModifier;
        _logger          = logger;
    }

    // ── Start: ApplySpeed only ────────────────────────────────────────────────

    public override void OnHeldInteractStart(
        ItemSlot slot, EntityAgent byEntity,
        BlockSelection blockSel, EntitySelection entitySel,
        bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (byEntity.World.Side != EnumAppSide.Server || _computeModifier == null) return;

        Entity target = ResolveTarget(byEntity, entitySel, slot);
        if (ShouldSkip(byEntity, target)) return;

        // Compute modifier using the current aim target. ApplySpeed is healer-trait-based
        // and target-independent, so this is correct even if aim shifts mid-cast.
        HealModifier mod = _computeModifier(byEntity, target);

        // Only touch the stat when the item actually reads it (AffectedByArmor) and
        // the trait modifier actually changes cast time.
        BehaviorHealingItem? healBehavior =
            collObj.GetCollectibleBehavior<BehaviorHealingItem>(withInheritance: true);

        if (mod.ApplySpeedMultiplier != 1.0f && healBehavior?.Config.AffectedByArmor == true)
        {
            // GetApplicationTime:  effectiveness = Clamp(GetBlended(), 0, 2) - 1
            // We need GetBlended() == ApplySpeedMultiplier.
            // WeightedSum base = 1.0, so we add:  delta = ApplySpeedMultiplier - 1
            // → GetBlended returns 1 + delta = ApplySpeedMultiplier
            // → Clamp(ApplySpeedMultiplier, 0, 2) - 1 = ApplySpeedMultiplier - 1 (the desired delta)
            float delta = mod.ApplySpeedMultiplier - 1.0f;
            byEntity.Stats.Set("healingeffectivness", "healinghands", delta, persistent: false);
            _statSetForHealer.Add(byEntity.EntityId);
        }

        // Do NOT set handling. Leave it PassThrough so the loop continues to
        // BehaviorHealingItem, which sets PreventSubsequent and takes control.
    }

    // ── Stop: HP + HoT speed ─────────────────────────────────────────────────

    public override void OnHeldInteractStop(
        float secondsUsed, ItemSlot slot, EntityAgent byEntity,
        BlockSelection blockSel, EntitySelection? entitySel,
        ref EnumHandling handling)
    {
        if (byEntity.World.Side != EnumAppSide.Server) return;

        // Schedule stat removal regardless of whether the heal fires.
        // Vanilla's Stop still reads GetApplicationTime as its completion guard:
        //   if (secondsUsed < GetApplicationTime(byEntity)) return;
        // The stat must remain set until after that check. A 0ms callback defers
        // removal to the next game tick, after the entire Stop chain completes.
        ScheduleStatRemoval(byEntity);

        // Re-resolve the target exactly as vanilla does in GetTargetEntity.
        // This is the entity that vanilla will heal — our target must match it.
        Entity target = ResolveTarget(byEntity, entitySel, slot);
        if (ShouldSkip(byEntity, target)) return;

        if (_computeModifier == null) return;
        HealModifier mod = _computeModifier(byEntity, target);
        if (mod.IsIdentity) return;

        // Install the modifier on the target's HealReceiveBehavior.
        // This behavior's Stop runs first in the loop (we are prepended).
        // BehaviorHealingItem's Stop runs immediately after us and calls ReceiveDamage,
        // which synchronously triggers HealReceiveBehavior.OnEntityReceiveDamage where
        // the modifier is consumed. The whole chain is on the main thread.
        HealReceiveBehavior? receiver = target.GetBehavior<HealReceiveBehavior>();
        if (receiver == null) return;

        receiver.SetPending(mod);

        _logger?.Notification(
            $"[HealingHands] {byEntity.GetName()} → {target.GetName()}: " +
            $"hp×{mod.HpMultiplier:F2}, hotSpeed×{mod.HealSpeedMultiplier:F2}, " +
            $"applySpeed×{mod.ApplySpeedMultiplier:F2}");
    }

    // ── Cancel: clean up without healing ─────────────────────────────────────

    public override bool OnHeldInteractCancel(
        float secondsUsed, ItemSlot slot, EntityAgent byEntity,
        BlockSelection blockSel, EntitySelection entitySel,
        EnumItemUseCancelReason cancelReason, ref EnumHandling handled)
    {
        if (byEntity.World.Side == EnumAppSide.Server)
        {
            ScheduleStatRemoval(byEntity);

            // Clear any pending context in case Stop fired but ReceiveDamage did not.
            // In practice Stop and Cancel are mutually exclusive, but be defensive.
            Entity target = ResolveTarget(byEntity, entitySel, slot);
            if (!ShouldSkip(byEntity, target))
                target.GetBehavior<HealReceiveBehavior>()?.ClearPending();
        }

        return base.OnHeldInteractCancel(
            secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason, ref handled);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when this heal should be completely ignored by the mod.
    /// Two cases: the target is the healer (self-heal), or the target is not a player
    /// and the config restricts to player-to-player heals.
    /// </summary>
    private static bool ShouldSkip(EntityAgent healer, Entity target)
    {
        if (target.EntityId == healer.EntityId) return true;  // self-heal
        if (target is not EntityPlayer) return true;           // non-player target
        return false;
    }

    /// <summary>
    /// Schedules removal of our <c>healingeffectivness</c> stat entry on the next game tick,
    /// but only if we actually set it for this healer during their current cast.
    /// Safe to call even if the stat was never set (HashSet.Remove returns false silently).
    /// </summary>
    private void ScheduleStatRemoval(EntityAgent byEntity)
    {
        if (!_statSetForHealer.Remove(byEntity.EntityId)) return;

        byEntity.World.RegisterCallback(_ =>
            byEntity.Stats.Remove("healingeffectivness", "healinghands"), 0);
    }

    /// <summary>
    /// Mirrors <c>BehaviorHealingItem.GetTargetEntity</c> exactly, so our resolved target
    /// always matches the entity vanilla will heal.
    /// </summary>
    private static Entity ResolveTarget(EntityAgent byEntity, EntitySelection? entitySel, ItemSlot slot)
    {
        if (entitySel?.Entity == null) return byEntity;
        if (!byEntity.Controls.CtrlKey) return byEntity;
        if (byEntity.Controls.Forward || byEntity.Controls.Backward ||
            byEntity.Controls.Left    || byEntity.Controls.Right) return byEntity;
        EntityBehaviorHealth? health = entitySel.Entity.GetBehavior<EntityBehaviorHealth>();
        return health != null && health.IsHealable(byEntity, slot) ? entitySel.Entity : byEntity;
    }
}
