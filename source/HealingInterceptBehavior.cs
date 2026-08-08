using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VSHealingHands;

/// <summary>
/// Prepended (index 0) onto every item/block whose CollectibleBehaviorHealingItem
/// actually restores health. Realizes all three healing modifiers without ever
/// mutating that shared behavior's own fields - unlike a mutate-then-revert-on-the-
/// shared-instance approach, nothing here is written by more than one logical actor,
/// so correctness does not depend on the server's threading model.
/// <br/><br/>
/// <b>Why prepend.</b> CollectibleBehaviorHealingItem.OnHeldInteractStart sets
/// <c>handling = EnumHandling.PreventSubsequent</c>, which stops the behavior loop
/// right after it runs. Anything added after it would never get its Start called, so
/// this has to come first: our Start before that PreventSubsequent, and our Stop
/// before vanilla's Stop applies the heal.
/// <br/><br/>
/// <b>Application speed (Start).</b> Vanilla decides cast time in
/// CollectibleBehaviorHealingItem.GetApplicationTime, which reads
/// byEntity.Stats.GetBlended("healingeffectivness") live. We add a temporary, keyed
/// entry to that stat so vanilla's own formula does the work, stacking on top of
/// whatever the healer already has from armor/food rather than replacing it.
/// <br/><br/>
/// <b>Removing that stat (Stop/Cancel).</b> Because we run before vanilla's own Stop
/// in the same call, and vanilla's Stop re-reads GetApplicationTime as its completion
/// guard, the stat must still be present when vanilla's Stop runs. Removal is
/// therefore deferred by one tick, guarded by a per-healer generation counter so a
/// second cast started in that single tick doesn't have its stat stripped by the first
/// cast's deferred removal.
/// <br/><br/>
/// <b>HP amount and healing speed (Stop).</b> These depend on the resolved target,
/// which vanilla only determines inside its own Stop via GetTargetEntity;
/// <see cref="ResolveTarget"/> mirrors that same logic here. The computed modifier is
/// handed to the target's own <see cref="HealReceiveBehavior"/>, which applies it when
/// vanilla's Stop - running immediately after, on the same call stack - triggers
/// ReceiveDamage.
/// </summary>
public sealed class HealingInterceptBehavior : CollectibleBehavior
{
    // Lets a client without the mod skip deserializing this behavior instead of
    // throwing "Don't know how to instantiate collectible behavior of class...".
    public override bool ClientSideOptional => true;

    // Supplied via the injection constructor; null on the registry constructor used
    // for client-side/deserialization instances, which never run any logic.
    readonly System.Func<Entity, Entity, HealingValues>? resolveValues;
    readonly ILogger? logger;

    // Per-healer generation counter guarding the deferred application-speed stat
    // removal - see the "Removing that stat" note above.
    readonly Dictionary<long, int> castGeneration = new();

    bool? affectedByArmor;

    // Registry constructor: client-side / deserialization, inert.
    public HealingInterceptBehavior(CollectibleObject collObj) : base(collObj) { }

    // Injection constructor: server-side, from VSHealingHandsModSystem.
    internal HealingInterceptBehavior(CollectibleObject collObj, System.Func<Entity, Entity, HealingValues> resolveValues, ILogger logger)
        : base(collObj)
    {
        this.resolveValues = resolveValues;
        this.logger = logger;
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (byEntity.World.Side != EnumAppSide.Server || resolveValues == null) return;

        Entity target = ResolveTarget(byEntity, entitySel, slot);
        if (ShouldSkip(byEntity, target)) return;

        HealingValues values = resolveValues(byEntity, target);

        // Setting the stat is pointless when the item ignores it entirely.
        if (values.ApplicationSpeedMultiplier != 1f && GetAffectedByArmor())
        {
            float delta = values.ApplicationSpeedMultiplier - 1f;
            byEntity.Stats.Set("healingeffectivness", "vshealinghands", delta, persistent: false);
            castGeneration[byEntity.EntityId] = castGeneration.GetValueOrDefault(byEntity.EntityId) + 1;
        }
    }

    public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection? entitySel, ref EnumHandling handling)
    {
        if (byEntity.World.Side != EnumAppSide.Server) return;

        ScheduleStatRemoval(byEntity);

        Entity target = ResolveTarget(byEntity, entitySel, slot);
        if (ShouldSkip(byEntity, target) || resolveValues == null) return;

        HealingValues values = resolveValues(byEntity, target);
        if (values.HealAmountMultiplier == 1f && values.HealingSpeedMultiplier == 1f) return;

        HealReceiveBehavior? receiver = target.GetBehavior<HealReceiveBehavior>();
        if (receiver == null) return;

        receiver.SetPending(values);

        logger?.Notification(
            $"[VSHealingHands] {byEntity.GetName()} -> {target.GetName()}: " +
            $"hp x{values.HealAmountMultiplier:F2}, healSpeed x{values.HealingSpeedMultiplier:F2}, " +
            $"applySpeed x{values.ApplicationSpeedMultiplier:F2}");
    }

    public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason, ref EnumHandling handled)
    {
        if (byEntity.World.Side == EnumAppSide.Server)
        {
            ScheduleStatRemoval(byEntity);

            // A cancelled cast can't apply a heal, but defensively drop any modifier
            // we may have queued (Stop and Cancel are mutually exclusive in practice).
            Entity target = ResolveTarget(byEntity, entitySel, slot);
            if (!ShouldSkip(byEntity, target))
            {
                target.GetBehavior<HealReceiveBehavior>()?.ClearPending();
            }
        }

        return base.OnHeldInteractCancel(secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason, ref handled);
    }

    /// <summary>True only for a player applying a heal item to a *different* player.</summary>
    static bool ShouldSkip(EntityAgent healer, Entity target)
    {
        if (target.EntityId == healer.EntityId) return true;
        if (target is not EntityPlayer) return true;
        return false;
    }

    /// <summary>
    /// Queues removal of the application-speed stat one tick from now (see the class
    /// doc comment for why it can't be removed immediately). Only removes it if the
    /// healer's generation is unchanged since scheduling - i.e. they have not started
    /// a new cast that re-set the stat in the meantime.
    /// </summary>
    void ScheduleStatRemoval(EntityAgent byEntity)
    {
        long id = byEntity.EntityId;
        if (!castGeneration.TryGetValue(id, out int generation)) return;

        byEntity.World.RegisterCallback(_ =>
        {
            if (castGeneration.TryGetValue(id, out int current) && current == generation)
            {
                byEntity.Stats.Remove("healingeffectivness", "vshealinghands");
                castGeneration.Remove(id);
            }
        }, 0);
    }

    /// <summary>
    /// GetApplicationTime ignores healingeffectivness entirely when the item's
    /// AffectedByArmor is false, so there's no point setting the stat in that case.
    /// Cached since the behavior list - and so this flag - is fixed per item type.
    /// </summary>
    bool GetAffectedByArmor()
    {
        if (affectedByArmor.HasValue) return affectedByArmor.Value;

        CollectibleBehaviorHealingItem? healBehavior = collObj.GetCollectibleBehavior<CollectibleBehaviorHealingItem>(withInheritance: true);
        affectedByArmor = healBehavior?.AffectedByArmor ?? true;
        return affectedByArmor.Value;
    }

    /// <summary>
    /// Resolves the heal target the way CollectibleBehaviorHealingItem.GetTargetEntity
    /// does: the aimed-at entity when the healer holds Ctrl, is stationary, and that
    /// entity is healable; otherwise the healer themselves. Kept in sync with vanilla
    /// so our resolved target always matches the entity vanilla actually heals.
    /// </summary>
    static Entity ResolveTarget(EntityAgent byEntity, EntitySelection? entitySel, ItemSlot slot)
    {
        if (entitySel?.Entity == null) return byEntity;
        if (!byEntity.Controls.CtrlKey) return byEntity;
        if (byEntity.Controls.Forward || byEntity.Controls.Backward || byEntity.Controls.Left || byEntity.Controls.Right) return byEntity;

        EntityBehaviorHealth? health = entitySel.Entity.GetBehavior<EntityBehaviorHealth>();
        return health != null && health.IsHealable(byEntity, slot) ? entitySel.Entity : byEntity;
    }
}
