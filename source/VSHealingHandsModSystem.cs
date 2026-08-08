using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

[assembly: ModInfo("VSHealingHands", "vshealinghands")]

namespace VSHealingHands;

/// <summary>
/// Entry point and lifecycle owner. When one player uses a healing item on another
/// player, the heal's HP amount, healing speed, and application speed are adjusted
/// based on the healer's character traits. Self-heals are never affected. See
/// OLD_VS_NEW_COMPARISON.md for why this uses native CollectibleBehavior/EntityBehavior
/// injection rather than patching vanilla methods.
/// </summary>
public class VSHealingHandsModSystem : ModSystem
{
    public static HealingHandsConfig? Config { get; private set; }

    ICoreServerAPI? sapi;
    CharacterSystem? characterSystem;

    // One HealReceiveBehavior instance per online player, kept so we can remove the
    // exact instance we added when the player leaves.
    readonly Dictionary<string, HealReceiveBehavior> receiveBehaviors = new();

    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        // Registered on both sides so item-type and entity data can deserialize
        // everywhere; every callback in both classes guards on EnumAppSide.Server, so
        // client-side instances are inert.
        api.RegisterCollectibleBehaviorClass("HealingInterceptBehavior", typeof(HealingInterceptBehavior));
        api.RegisterEntityBehaviorClass("vshealinghands:healreceive", typeof(HealReceiveBehavior));
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        Config = api.LoadModConfig<HealingHandsConfig>("vshealinghandsconfig.json");
        if (Config == null)
        {
            Config = new HealingHandsConfig();
            api.StoreModConfig(Config, "vshealinghandsconfig.json");
        }

        characterSystem = api.ModLoader.GetModSystem<CharacterSystem>();

        api.Event.PlayerNowPlaying += OnPlayerJoin;
        api.Event.PlayerLeave += OnPlayerLeave;
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        // Injection is server-only; clients receive the updated behavior list via
        // the normal item-type sync. By AssetsFinalize the registries are populated.
        if (api.Side != EnumAppSide.Server) return;

        int injected = 0;
        foreach (Item item in api.World.Items) if (TryInjectInterceptBehavior(item)) injected++;
        foreach (Block block in api.World.Blocks) if (TryInjectInterceptBehavior(block)) injected++;

        api.Logger.Notification($"[VSHealingHands] Prepended intercept behavior into {injected} healing collectible type(s).");
    }

    public override void Dispose()
    {
        if (sapi != null)
        {
            sapi.Event.PlayerNowPlaying -= OnPlayerJoin;
            sapi.Event.PlayerLeave -= OnPlayerLeave;
        }
        receiveBehaviors.Clear();
    }

    // ── Modifier resolution ──────────────────────────────────────────────────

    HealingValues ResolveValues(Entity healer, Entity target)
    {
        if (Config == null || healer is not EntityPlayer healerPlayer) return new HealingValues();
        return Config.Resolve(GetPresentTraitCodes(healerPlayer, Config));
    }

    IEnumerable<string> GetPresentTraitCodes(EntityPlayer healer, HealingHandsConfig config)
    {
        if (characterSystem == null) yield break;

        foreach (string traitCode in config.Traits.Keys)
        {
            if (characterSystem.HasTrait(healer.Player, traitCode))
            {
                yield return traitCode;
            }
        }
    }

    // ── Player events ────────────────────────────────────────────────────────

    void OnPlayerJoin(IServerPlayer player)
    {
        if (player.Entity == null)
        {
            sapi?.Logger.Warning($"[VSHealingHands] '{player.PlayerName}': no entity on join - HealReceiveBehavior not added.");
            return;
        }

        if (player.Entity.GetBehavior<HealReceiveBehavior>() != null) return;

        HealReceiveBehavior behavior = new(player.Entity);

        // Deliberately not AddBehavior (which appends): EntityBehaviorHealth is
        // already in the list from the entity's own JSON, and it must NOT get first
        // look at a heal - see HealReceiveBehavior's doc comment for why. Insert(0, ..)
        // plus CacheServerBehaviors() mirrors what AddBehavior does internally, just
        // at the front instead of the back.
        player.Entity.SidedProperties.Behaviors.Insert(0, behavior);
        player.Entity.CacheServerBehaviors();

        receiveBehaviors[player.PlayerUID] = behavior;
    }

    void OnPlayerLeave(IServerPlayer player)
    {
        if (!receiveBehaviors.Remove(player.PlayerUID, out HealReceiveBehavior? behavior)) return;
        player.Entity?.RemoveBehavior(behavior);
    }

    // ── CollectibleBehavior injection ───────────────────────────────────────

    /// <summary>
    /// Prepends a HealingInterceptBehavior onto <paramref name="col"/> if it is a
    /// healing item that has not already been injected. Returns true if injection
    /// happened.
    /// </summary>
    bool TryInjectInterceptBehavior(CollectibleObject? col)
    {
        if (col == null) return false;
        if (col.GetCollectibleBehavior<HealingInterceptBehavior>(withInheritance: false) != null) return false;

        CollectibleBehaviorHealingItem? healBehavior = col.GetCollectibleBehavior<CollectibleBehaviorHealingItem>(withInheritance: true);
        if (healBehavior == null || healBehavior.Health <= 0f) return false;

        HealingInterceptBehavior intercept = new(col, ResolveValues, sapi!.Logger);

        // Prepend at index 0 - required so our Start runs before
        // CollectibleBehaviorHealingItem's Start sets EnumHandling.PreventSubsequent
        // (which would otherwise stop the loop before we run), and so our Stop runs
        // before vanilla's Stop calls ReceiveDamage.
        CollectibleBehavior[] old = col.CollectibleBehaviors;
        CollectibleBehavior[] updated = new CollectibleBehavior[old.Length + 1];
        updated[0] = intercept;
        Array.Copy(old, 0, updated, 1, old.Length);
        col.CollectibleBehaviors = updated;
        return true;
    }
}
