using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
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
/// <para><b>Compile-time independence from BehaviorHealingItem.</b><br/>
/// BehaviorHealingItem is not directly referenceable in VS 1.22 (it became an internal
/// type in VSSurvivalMod). All access uses runtime type-name matching and JSON parsing
/// of propertiesAtString so the code compiles against both 1.21 and 1.22.</para>
///
/// <para><b>Self-heal: complete no-op.</b><br/>
/// ShouldSkip detects target == healer and returns from both Start and Stop without
/// touching any state.</para>
///
/// <para><b>Isolation across concurrent uses of the same item type.</b><br/>
/// This behavior is one instance per item type. State is stored in _statSetForHealer,
/// a HashSet keyed by healer entity ID. HealReceiveBehavior instances are per-target-entity
/// and hold a single nullable HealModifier field.</para>
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

    // Tracks which healer entity IDs have had their healingeffectivness stat modified
    // by us during the current cast, so we only schedule removal for stats we actually set.
    private readonly HashSet<long> _statSetForHealer = new();

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

        // Only touch the stat when the item reads it (AffectedByArmor == true in the
        // behavior's JSON config) and the trait modifier changes cast time.
        // We locate the healing behavior and read AffectedByArmor at runtime to avoid
        // a compile-time dependency on BehaviorHealingItem (inaccessible in VS 1.22).
        if (mod.ApplySpeedMultiplier != 1.0f && GetHealingBehaviorAffectedByArmor())
        {
            // GetApplicationTime:  effectiveness = Clamp(GetBlended(), 0, 2) - 1
            // We need GetBlended() == ApplySpeedMultiplier.
            // WeightedSum base = 1.0, so we add:  delta = ApplySpeedMultiplier - 1
            // → GetBlended returns 1 + delta = ApplySpeedMultiplier
            float delta = mod.ApplySpeedMultiplier - 1.0f;
            byEntity.Stats.Set("healingeffectivness", "healinghands", delta, persistent: false);
            _statSetForHealer.Add(byEntity.EntityId);
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
        if (!_statSetForHealer.Remove(byEntity.EntityId)) return;
        byEntity.World.RegisterCallback(_ =>
            byEntity.Stats.Remove("healingeffectivness", "healinghands"), 0);
    }

    /// <summary>
    /// Returns the AffectedByArmor flag from the item's CollectibleBehaviorHealingItem config.
    /// In VS 1.22, CollectibleBehaviorHealingItem is a public accessible type in
    /// Vintagestory.GameContent (renamed from BehaviorHealingItem in 1.21).
    /// Result is cached after the first call since the behavior list is fixed per item type.
    /// Defaults to true if the behavior or property cannot be found.
    /// </summary>
    private bool GetHealingBehaviorAffectedByArmor()
    {
        if (_affectedByArmor.HasValue) return _affectedByArmor.Value;

        BehaviorHealingItem? healBehavior =
            collObj.GetCollectibleBehavior<BehaviorHealingItem>(withInheritance: true);

        if (healBehavior == null)
        {
            _affectedByArmor = true;
            return true;
        }

        // AffectedByArmor is not a direct property on CollectibleBehaviorHealingItem but is
        // still stored in the JSON config. Read it from propertiesAtString — the raw JSON
        // stored by CollectibleBehavior.Initialize(). Defaults to true (matching the vanilla default).
        if (!string.IsNullOrWhiteSpace(healBehavior.propertiesAtString))
        {
            try
            {
                JsonObject props = JsonObject.FromJson(healBehavior.propertiesAtString);
                _affectedByArmor = props["affectedByArmor"].AsBool(defaultValue: true);
                return _affectedByArmor.Value;
            }
            catch { /* malformed JSON — fall through to default */ }
        }

        _affectedByArmor = true;
        return true;
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
