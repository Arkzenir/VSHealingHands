using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace HealingHands.Systems;

/// <summary>
/// <see cref="CollectibleBehavior"/> prepended at index 0 into every healing item.
/// Handles all three healing parameters without mutating the shared
/// BehaviorHealingItem Config object.
///
/// <para><b>Why prepend?</b><br/>
/// BehaviorHealingItem.OnHeldInteractStart sets
/// handling = EnumHandling.PreventSubsequent, aborting the behavior loop immediately.
/// Any behavior appended after BehaviorHealingItem never has its Start called.
/// Prepending ensures our Start fires before PreventSubsequent is set.</para>
///
/// <para><b>ApplySpeed — healingeffectivness stat</b><br/>
/// BehaviorHealingItem.GetApplicationTime reads
/// entity.Stats.GetBlended("healingeffectivness") live on every Step tick and again
/// in Stop's completion guard. We write a trait-derived delta to that stat in Start
/// so vanilla's formula produces the modified cast duration automatically. The stat is
/// removed in a 0ms RegisterCallback from Stop/Cancel, deferring cleanup to the next
/// game tick after vanilla's Stop has read the stat for its guard check.</para>
///
/// <para><b>HP + HoT speed</b><br/>
/// These are set at Stop time, not Start, because the final target is only confirmed
/// when vanilla calls GetTargetEntity inside its own Stop. We mirror that resolution,
/// re-compute the modifier, and call HealReceiveBehavior.SetPending on the target's
/// behavior immediately before vanilla's Stop runs. Vanilla then calls ReceiveDamage
/// synchronously, which triggers HealReceiveBehavior.OnEntityReceiveDamage where the
/// modifier is consumed.</para>
///
/// <para><b>Self-heal: complete no-op.</b><br/>
/// ShouldSkip detects target == healer and returns from both Start and Stop without
/// touching any state.</para>
///
/// <para><b>Isolation across concurrent uses of the same item type.</b><br/>
/// This behavior is one instance per item type. State is stored in _castGeneration,
/// a per-healer generation counter that guards against the stat-removal stacking race.
/// HealReceiveBehavior instances are per-target-entity and hold a single nullable
/// HealModifier field.</para>
/// </summary>
public sealed class HealingInterceptBehavior : CollectibleBehavior
{
    // The client never executes any logic here — all callbacks guard on EnumAppSide.Server.
    // ClientSideOptional = true tells VS to skip this behavior during item-type packet
    // deserialization on clients that don't have the mod loaded, rather than crashing with
    // "Don't know how to instantiate collectible behavior of class 'HealingInterceptBehavior'".
    public override bool ClientSideOptional => true;

    private readonly System.Func<Entity, Entity, HealModifier>? _computeModifier;
    private readonly ILogger? _logger;

    // Tracks the active cast "generation" per healer entity ID. Each time a healer begins
    // a cast that sets our stat, the generation is incremented. The deferred removal callback
    // captures the generation at schedule time and only removes the stat if it still matches —
    // i.e. the healer has not begun a newer cast in the meantime. This prevents the
    // stacking race where a quickly-restarted cast has its stat removed early by the previous
    // cast's pending callback (the same class of bug documented in CombatSlowExtended).
    private readonly Dictionary<long, int> _castGeneration = new();

    // Cached result of GetHealingBehaviorAffectedByArmor() — constant per item type.
    // null = not yet evaluated, true/false = result.
    private bool? _affectedByArmor;

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

        HealModifier mod = _computeModifier(byEntity, target);

        // Only touch the stat when the item actually reads it (AffectedByArmor == true)
        // and the trait modifier changes cast time. GetApplicationTime ignores the stat
        // entirely when AffectedByArmor is false, so setting it then would be wasted work.
        if (mod.ApplySpeedMultiplier != 1.0f && GetHealingBehaviorAffectedByArmor())
        {
            // GetApplicationTime:  effectiveness = Clamp(GetBlended(), 0, 2) - 1
            // We need GetBlended() == ApplySpeedMultiplier.
            // WeightedSum base = 1.0, so we add:  delta = ApplySpeedMultiplier - 1
            // → GetBlended returns 1 + delta = ApplySpeedMultiplier
            float delta = mod.ApplySpeedMultiplier - 1.0f;
            byEntity.Stats.Set("healingeffectivness", "healinghands", delta, persistent: false);
            _castGeneration[byEntity.EntityId] = _castGeneration.GetValueOrDefault(byEntity.EntityId) + 1;
        }

        // Do NOT set handling — leave PassThrough so the loop continues to
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
        // Vanilla's Stop still reads GetApplicationTime as its completion guard and
        // the stat must remain set until that check completes. The 0ms callback defers
        // removal to the next game tick, after the entire Stop chain finishes.
        ScheduleStatRemoval(byEntity);

        Entity target = ResolveTarget(byEntity, entitySel, slot);
        if (ShouldSkip(byEntity, target)) return;

        if (_computeModifier == null) return;
        HealModifier mod = _computeModifier(byEntity, target);
        if (mod.IsIdentity) return;

        // Install the modifier on the target's HealReceiveBehavior before vanilla's Stop.
        // We are prepended, so our Stop fires first. BehaviorHealingItem's Stop fires
        // immediately after and calls ReceiveDamage synchronously on the same call stack,
        // where HealReceiveBehavior.OnEntityReceiveDamage consumes the modifier.
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

            Entity target = ResolveTarget(byEntity, entitySel, slot);
            if (!ShouldSkip(byEntity, target))
                target.GetBehavior<HealReceiveBehavior>()?.ClearPending();
        }

        return base.OnHeldInteractCancel(
            secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason, ref handled);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool ShouldSkip(EntityAgent healer, Entity target)
    {
        if (target.EntityId == healer.EntityId) return true;  // self-heal
        if (target is not EntityPlayer) return true;           // non-player target
        return false;
    }

    private void ScheduleStatRemoval(EntityAgent byEntity)
    {
        long id = byEntity.EntityId;

        // Only schedule removal if we actually set the stat for this healer.
        if (!_castGeneration.TryGetValue(id, out int generation)) return;

        // Defer removal to the next game tick (0ms) so vanilla's Stop guard, which reads
        // GetApplicationTime (and thus the stat), still sees our value during the current
        // Stop chain. The callback captures the generation at schedule time: if the healer
        // begins a new cast before the callback fires, the generation will have advanced
        // and we skip removal so the newer cast keeps its stat.
        byEntity.World.RegisterCallback(_ =>
        {
            if (_castGeneration.TryGetValue(id, out int current) && current == generation)
            {
                byEntity.Stats.Remove("healingeffectivness", "healinghands");
                _castGeneration.Remove(id);
            }
        }, 0);
    }

    /// <summary>
    /// Returns the AffectedByArmor flag from the item's BehaviorHealingItem config.
    /// In VS 1.21, BehaviorHealingItem is a public type in Vintagestory.GameContent
    /// exposing a typed Config (HealOverTimeConfig). Result is cached after the first
    /// call since the behavior list is fixed per item type.
    /// Defaults to true if the behavior cannot be found.
    /// </summary>
    private bool GetHealingBehaviorAffectedByArmor()
    {
        if (_affectedByArmor.HasValue) return _affectedByArmor.Value;

        BehaviorHealingItem? healBehavior =
            collObj.GetCollectibleBehavior<BehaviorHealingItem>(withInheritance: true);

        _affectedByArmor = healBehavior?.Config.AffectedByArmor ?? true;
        return _affectedByArmor.Value;
    }

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
