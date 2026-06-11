using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace HealingHands.Systems;

/// <summary>
/// A <see cref="CollectibleBehavior"/> prepended (index 0) onto every healing item.
/// It realizes all three healing modifiers without ever mutating the item's shared
/// <c>BehaviorHealingItem.Config</c> object.
///
/// <para><b>Why prepend.</b> <c>BehaviorHealingItem.OnHeldInteractStart</c> sets
/// <c>handling = EnumHandling.PreventSubsequent</c>, which stops the behavior loop right
/// after it runs. Anything added after <c>BehaviorHealingItem</c> would therefore never
/// get its <c>Start</c> called. Prepending guarantees our callbacks run first: our
/// <c>Start</c> before that <c>PreventSubsequent</c>, and our <c>Stop</c> before vanilla's
/// <c>Stop</c> applies the heal.</para>
///
/// <para><b>Application speed (set in Start).</b> Vanilla decides cast time in
/// <c>BehaviorHealingItem.GetApplicationTime</c>, which reads
/// <c>byEntity.Stats.GetBlended("healingeffectivness")</c> live every tick. We add a
/// temporary entry to that stat keyed <c>"healinghands"</c>, so vanilla's own formula
/// shortens or lengthens the cast for us. Because the stat is keyed, our entry adds on top
/// of whatever the healer already has from gear or food rather than replacing it. The entry
/// is removed by a deferred callback (see <see cref="ScheduleStatRemoval"/>).</para>
///
/// <para><b>HP and heal-over-time speed (set in Stop).</b> These depend on the final heal
/// target, which vanilla only resolves inside its own <c>Stop</c> via <c>GetTargetEntity</c>.
/// We mirror that resolution in <see cref="ResolveTarget"/>, compute the modifier, and call
/// <see cref="HealReceiveBehavior.SetPending"/> on the target. Vanilla's <c>Stop</c> then runs
/// (immediately after ours, on the same synchronous call stack) and calls
/// <c>ReceiveDamage</c>, where <see cref="HealReceiveBehavior.OnEntityReceiveDamage"/> consumes
/// the pending modifier and applies the HP and HoT-speed changes.</para>
///
/// <para><b>Self-heals are a no-op.</b> <see cref="ShouldSkip"/> returns true when the
/// resolved target is the healer (or, depending on config, a non-player), and both
/// <c>Start</c> and <c>Stop</c> return before touching any state.</para>
///
/// <para><b>Concurrency.</b> One instance of this behavior is shared across all items of a
/// type, but the only per-use state it holds is <see cref="_castGeneration"/>, keyed by
/// healer entity id, which guards the application-speed stat against an early-removal race
/// (see <see cref="ScheduleStatRemoval"/>). The per-target HP/HoT context lives on each
/// player's own <see cref="HealReceiveBehavior"/>, so two players healing two different
/// targets never share state.</para>
/// </summary>
public sealed class HealingInterceptBehavior : CollectibleBehavior
{
    // This behavior is server-only; every callback returns early on the client. Marking it
    // ClientSideOptional lets clients that don't have the mod skip it when deserializing
    // item-type packets instead of throwing "Don't know how to instantiate collectible
    // behavior of class 'HealingInterceptBehavior'".
    public override bool ClientSideOptional => true;

    // Supplied via the injection constructor; null on registry-created instances (client/
    // deserialization), which never run any logic.
    private readonly System.Func<Entity, Entity, HealModifier>? _computeModifier;
    private readonly ILogger? _logger;

    // Per-healer "generation" counter for the application-speed stat. Each cast that sets the
    // stat bumps the healer's generation; the deferred removal callback captures the
    // generation it was scheduled for and only removes the stat if it still matches. This
    // prevents the race where a healer finishes one cast and starts another within the single
    // tick before the first cast's removal callback fires — without the guard, the first
    // callback would strip the second cast's stat. (Same class of bug as CombatSlowExtended.)
    private readonly Dictionary<long, int> _castGeneration = new();

    // Cached AffectedByArmor flag for this item type. The behavior list is fixed per type, so
    // this only needs resolving once. null = not yet resolved.
    private bool? _affectedByArmor;

    // ── Registry constructor (client / deserialization; inert) ────────────────

    public HealingInterceptBehavior(CollectibleObject collObj) : base(collObj) { }

    // ── Injection constructor (server-side, from HealingHandsModSystem) ───────

    internal HealingInterceptBehavior(
        CollectibleObject collObj,
        System.Func<Entity, Entity, HealModifier> computeModifier,
        ILogger logger)
        : base(collObj)
    {
        _computeModifier = computeModifier;
        _logger          = logger;
    }

    // ── Start: application speed ──────────────────────────────────────────────

    public override void OnHeldInteractStart(
        ItemSlot slot, EntityAgent byEntity,
        BlockSelection blockSel, EntitySelection entitySel,
        bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (byEntity.World.Side != EnumAppSide.Server || _computeModifier == null) return;

        Entity target = ResolveTarget(byEntity, entitySel, slot);
        if (ShouldSkip(byEntity, target)) return;

        HealModifier mod = _computeModifier(byEntity, target);

        // Set the stat only when it would actually change the cast and the item reads it.
        // GetApplicationTime ignores healingeffectivness when AffectedByArmor is false, so
        // setting it in that case would do nothing.
        if (mod.ApplySpeedMultiplier != 1.0f && GetHealingBehaviorAffectedByArmor())
        {
            // GetApplicationTime computes:  effectiveness = Clamp(GetBlended(), 0, 2) - 1.
            // We want effectiveness to equal (ApplySpeedMultiplier - 1), so GetBlended() must
            // return ApplySpeedMultiplier. The stat blends as a weighted sum over a base of
            // 1.0, so adding an entry of (ApplySpeedMultiplier - 1) yields exactly that — and,
            // being a separate keyed entry, it stacks on top of any existing modifiers.
            float delta = mod.ApplySpeedMultiplier - 1.0f;
            byEntity.Stats.Set("healingeffectivness", "healinghands", delta, persistent: false);
            _castGeneration[byEntity.EntityId] = _castGeneration.GetValueOrDefault(byEntity.EntityId) + 1;
        }

        // Leave handling as PassThrough so the loop proceeds to BehaviorHealingItem, which
        // takes over the interaction (setting PreventSubsequent).
    }

    // ── Stop: HP and heal-over-time speed ─────────────────────────────────────

    public override void OnHeldInteractStop(
        float secondsUsed, ItemSlot slot, EntityAgent byEntity,
        BlockSelection blockSel, EntitySelection? entitySel,
        ref EnumHandling handling)
    {
        if (byEntity.World.Side != EnumAppSide.Server) return;

        // Schedule removal of the application-speed stat. This runs whether or not the heal
        // actually fires, because vanilla's own Stop still calls GetApplicationTime (reading
        // the stat) as its completion guard, so the stat must survive until the current Stop
        // chain finishes — hence the deferred (next-tick) removal.
        ScheduleStatRemoval(byEntity);

        Entity target = ResolveTarget(byEntity, entitySel, slot);
        if (ShouldSkip(byEntity, target)) return;

        if (_computeModifier == null) return;
        HealModifier mod = _computeModifier(byEntity, target);
        if (mod.IsIdentity) return;

        // Hand the modifier to the target. We run before vanilla's Stop (we are prepended),
        // and vanilla's Stop then calls ReceiveDamage synchronously on this same call stack,
        // where the target's HealReceiveBehavior consumes what we set here.
        HealReceiveBehavior? receiver = target.GetBehavior<HealReceiveBehavior>();
        if (receiver == null) return;

        receiver.SetPending(mod);

        _logger?.Notification(
            $"[HealingHands] {byEntity.GetName()} → {target.GetName()}: " +
            $"hp×{mod.HpMultiplier:F2}, hotSpeed×{mod.HealSpeedMultiplier:F2}, " +
            $"applySpeed×{mod.ApplySpeedMultiplier:F2}");
    }

    // ── Cancel: clean up, apply nothing ───────────────────────────────────────

    public override bool OnHeldInteractCancel(
        float secondsUsed, ItemSlot slot, EntityAgent byEntity,
        BlockSelection blockSel, EntitySelection entitySel,
        EnumItemUseCancelReason cancelReason, ref EnumHandling handled)
    {
        if (byEntity.World.Side == EnumAppSide.Server)
        {
            ScheduleStatRemoval(byEntity);

            // Drop any modifier we may have queued on the target so a cancelled cast can't
            // leak into a later heal. (Stop and Cancel are mutually exclusive in practice;
            // this is defensive.)
            Entity target = ResolveTarget(byEntity, entitySel, slot);
            if (!ShouldSkip(byEntity, target))
                target.GetBehavior<HealReceiveBehavior>()?.ClearPending();
        }

        return base.OnHeldInteractCancel(
            secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason, ref handled);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the heal should be ignored entirely. The mod only acts on a player healing
    /// another player, so this returns true for a self-heal (the target is the healer) and
    /// for any non-player target.
    /// </summary>
    private static bool ShouldSkip(EntityAgent healer, Entity target)
    {
        if (target.EntityId == healer.EntityId) return true;  // self-heal
        if (target is not EntityPlayer) return true;           // non-player target
        return false;
    }

    /// <summary>
    /// Queues removal of the application-speed stat on the next game tick (0 ms delay).
    /// The delay is required because vanilla's Stop, which runs after ours, still reads the
    /// stat via GetApplicationTime. The callback only removes the stat if the healer's
    /// generation is unchanged since scheduling — i.e. they have not started a new cast that
    /// re-set the stat in the meantime.
    /// </summary>
    private void ScheduleStatRemoval(EntityAgent byEntity)
    {
        long id = byEntity.EntityId;

        // Nothing to remove if we never set the stat for this healer.
        if (!_castGeneration.TryGetValue(id, out int generation)) return;

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
    /// Reads and caches the item's <c>AffectedByArmor</c> flag from its
    /// <c>BehaviorHealingItem.Config</c>. When false, vanilla's GetApplicationTime ignores the
    /// <c>healingeffectivness</c> stat, so there is no point setting it. Defaults to true if
    /// the healing behavior can't be found.
    /// </summary>
    private bool GetHealingBehaviorAffectedByArmor()
    {
        if (_affectedByArmor.HasValue) return _affectedByArmor.Value;

        CollectibleBehaviorHealingItem? healBehavior =
            collObj.GetCollectibleBehavior<CollectibleBehaviorHealingItem>(withInheritance: true);

        // AffectedByArmor is not a direct property on CollectibleBehaviorHealingItem;
        // read it from propertiesAtString — the raw JSON stored by Initialize().
        if (healBehavior == null) { _affectedByArmor = true; return true; }
        if (!string.IsNullOrWhiteSpace(healBehavior.propertiesAtString))
        {
            try
            {
                var props = Vintagestory.API.Datastructures.JsonObject.FromJson(healBehavior.propertiesAtString);
                _affectedByArmor = props["affectedByArmor"].AsBool(defaultValue: true);
                return _affectedByArmor.Value;
            }
            catch { }
        }
        _affectedByArmor = true;
        return _affectedByArmor.Value;
    }

    /// <summary>
    /// Resolves the heal target the way <c>BehaviorHealingItem.GetTargetEntity</c> does:
    /// the aimed-at entity when the healer holds Ctrl, is stationary, and that entity is
    /// healable; otherwise the healer themselves. Keeping this in sync with vanilla ensures
    /// our resolved target matches the entity vanilla actually heals.
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
