using HealingHands.Commands;
using HealingHands.Systems;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace HealingHands;

/// <summary>
/// Entry point and lifecycle owner for the HealingHands mod.
///
/// <para><b>What the mod does.</b> When one player uses a healing item on another player,
/// the heal's HP amount, heal-over-time speed, and application (cast) speed are adjusted
/// based on the healer's character traits. Self-heals are never affected. The mod is
/// server-authoritative and requires no client installation.</para>
///
/// <para><b>How it works.</b> Two cooperating behaviors do the work, and neither ever
/// mutates the shared <c>BehaviorHealingItem.Config</c> object:</para>
/// <list type="number">
///   <item>
///     <term><see cref="HealingInterceptBehavior"/> — a CollectibleBehavior prepended onto
///     every healing item</term>
///     <description>
///       In <c>Start</c> it sets a temporary <c>healingeffectivness</c> stat on the healer
///       so vanilla's own application-time formula produces the modified cast speed.
///       In <c>Stop</c> it hands the resolved modifier to the target's
///       <see cref="HealReceiveBehavior"/> just before vanilla applies the heal.
///     </description>
///   </item>
///   <item>
///     <term><see cref="HealReceiveBehavior"/> — an EntityBehavior on every player</term>
///     <description>
///       In <c>OnEntityReceiveDamage</c> it scales the HP amount and heal-over-time speed
///       of the pending heal before the health system applies them.
///     </description>
///   </item>
/// </list>
///
/// <para><b>Lifecycle.</b> <see cref="Start"/> registers both behavior classes and loads the
/// config. <see cref="AssetsFinalize"/> injects <see cref="HealingInterceptBehavior"/> into
/// every healing collectible. <see cref="StartServerSide"/> registers commands and wires up
/// player join/leave so each player gets a <see cref="HealReceiveBehavior"/>.</para>
/// </summary>
public class HealingHandsModSystem : ModSystem
{
    // ── Public ────────────────────────────────────────────────────────────────

    /// <summary>The active config. Backed by the current system instance so a reload is visible immediately.</summary>
    public HealingHandsConfig Config => _system?.Config ?? _pendingConfig;

    // ── Private ───────────────────────────────────────────────────────────────

    private ICoreServerAPI? _serverApi;
    private HealingHandsSystem? _system;
    private HealingHandsConfig _pendingConfig = new();

    // One HealReceiveBehavior instance per online player, kept so we can remove the exact
    // instance we added when the player leaves.
    private readonly Dictionary<string, HealReceiveBehavior> _receiveBehaviors = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void Start(ICoreAPI api)
    {
        // Register both classes by name on whichever side this runs. The names must exist in
        // the registry so item-type and entity network packets can be deserialized; on a
        // client that has the mod, the instances created this way are inert because every
        // callback guards on EnumAppSide.Server. (Clients without the mod skip
        // HealingInterceptBehavior entirely thanks to its ClientSideOptional override.)
        api.RegisterCollectibleBehaviorClass("HealingInterceptBehavior", typeof(HealingInterceptBehavior));
        api.RegisterEntityBehaviorClass("healinghands:healreceive",      typeof(HealReceiveBehavior));

        if (api is not ICoreServerAPI sapi) return;

        _serverApi     = sapi;
        _pendingConfig = LoadConfig(sapi);
        _system        = new HealingHandsSystem(sapi, _pendingConfig);
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        // Injection is server-only; clients receive the updated behavior list via packets.
        // By AssetsFinalize the full item and block registries are populated.
        if (api.Side != EnumAppSide.Server || _system == null) return;

        int injected = 0;
        foreach (Item  item  in api.World.Items)  if (TryInjectInterceptBehavior(item))  injected++;
        foreach (Block block in api.World.Blocks) if (TryInjectInterceptBehavior(block)) injected++;

        api.Logger.Notification(
            $"[HealingHands] Prepended intercept behavior into {injected} healing collectible type(s).");
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        HealingHandsCommands.Register(api, this);
        api.Event.PlayerNowPlaying += OnPlayerJoin;
        api.Event.PlayerLeave      += OnPlayerLeave;
    }

    public override void Dispose()
    {
        if (_serverApi != null)
        {
            _serverApi.Event.PlayerNowPlaying -= OnPlayerJoin;
            _serverApi.Event.PlayerLeave      -= OnPlayerLeave;
        }
        _receiveBehaviors.Clear();
    }

    // ── Config ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reloads the config from disk and swaps in a fresh <see cref="HealingHandsSystem"/>.
    /// Existing behaviors call back through the modifier delegate, which reads the current
    /// system instance, so they pick up the new config on the next heal with no re-injection.
    /// </summary>
    internal void ReloadConfig(ICoreServerAPI api)
    {
        _pendingConfig = LoadConfig(api);
        _system = new HealingHandsSystem(api, _pendingConfig);
        api.Logger.Notification("[HealingHands] Config reloaded.");
    }

    private static HealingHandsConfig LoadConfig(ICoreServerAPI api)
    {
        const string filename = "healinghands.json";
        HealingHandsConfig? cfg = null;
        try { cfg = api.LoadModConfig<HealingHandsConfig>(filename); }
        catch (Exception ex)
        {
            api.Logger.Error($"[HealingHands] Failed to parse {filename}: {ex.Message} — using defaults.");
        }

        if (cfg == null)
        {
            cfg = HealingHandsConfig.CreateDefault();
            api.StoreModConfig(cfg, filename);
            api.Logger.Notification($"[HealingHands] Config not found — defaults written to ModConfig/{filename}.");
        }
        else
        {
            api.Logger.Notification("[HealingHands] Config loaded.");
        }
        return cfg;
    }

    // ── Player events ──────────────────────────────────────────────────────────

    private void OnPlayerJoin(IServerPlayer player)
    {
        if (player.Entity == null)
        {
            _serverApi?.Logger.Warning(
                $"[HealingHands] '{player.PlayerName}': no entity on join — HealReceiveBehavior not added.");
            return;
        }

        // Every player carries a HealReceiveBehavior so that heals targeting them can be
        // intercepted in OnEntityReceiveDamage.
        HealReceiveBehavior behavior = new(player.Entity);
        player.Entity.AddBehavior(behavior);
        _receiveBehaviors[player.PlayerUID] = behavior;
    }

    private void OnPlayerLeave(IServerPlayer player)
    {
        if (!_receiveBehaviors.Remove(player.PlayerUID, out HealReceiveBehavior? behavior)) return;
        player.Entity?.RemoveBehavior(behavior);
    }

    // ── CollectibleBehavior injection ──────────────────────────────────────────

    /// <summary>
    /// Prepends a <see cref="HealingInterceptBehavior"/> onto <paramref name="col"/> if it is
    /// a healing item that has not already been injected. Returns true if injection happened.
    /// </summary>
    private bool TryInjectInterceptBehavior(CollectibleObject? col)
    {
        if (col == null) return false;
        if (col.GetCollectibleBehavior<HealingInterceptBehavior>(withInheritance: false) != null) return false;

        // In VS 1.22 the class was renamed to CollectibleBehaviorHealingItem and Health
        // became a direct property. Only inject into items that actually restore health.
        CollectibleBehaviorHealingItem? healBehavior =
            col.GetCollectibleBehavior<CollectibleBehaviorHealingItem>(withInheritance: true);
        if (healBehavior == null || healBehavior.Health <= 0f) return false;

        HealingInterceptBehavior intercept = new(
            col,
            (healer, target) => _system!.ComputeModifier(healer, target),
            _serverApi!.Logger);

        // Prepend at index 0. This is required so our Start runs before BehaviorHealingItem's
        // Start sets EnumHandling.PreventSubsequent (which would otherwise abort the loop
        // before we run), and so our Stop runs before vanilla's Stop calls ReceiveDamage.
        CollectibleBehavior[] old     = col.CollectibleBehaviors;
        CollectibleBehavior[] updated = new CollectibleBehavior[old.Length + 1];
        updated[0] = intercept;
        Array.Copy(old, 0, updated, 1, old.Length);
        col.CollectibleBehaviors = updated;
        return true;
    }
}
