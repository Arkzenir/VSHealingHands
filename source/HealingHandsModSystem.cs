using HealingHands.Commands;
using HealingHands.Systems;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace HealingHands;

/// <summary>
/// Entry point for the HealingHands mod.
///
/// <para><b>Architecture</b></para>
/// <para>
/// Two complementary mechanisms handle the three healing parameters.
/// The shared <c>BehaviorHealingItem.Config</c> object is <b>never mutated</b>.
/// </para>
///
/// <list type="number">
///   <item>
///     <term><see cref="HealingInterceptBehavior"/> — CollectibleBehavior, prepended at index 0</term>
///     <description>
///       Runs before <c>BehaviorHealingItem</c> on every callback.<br/>
///       • <b>Start:</b> resolves target, computes modifier, sets a
///       <c>healingeffectivness</c> stat delta on the healer so vanilla's
///       <c>GetApplicationTime</c> produces the modified cast duration.<br/>
///       • <b>Stop:</b> installs a <see cref="HealReceiveBehavior.SetPending"/> context on the
///       target <em>before</em> vanilla's Stop calls <c>ReceiveDamage</c>, then schedules a
///       0 ms callback to remove the stat after the full Stop chain completes.<br/>
///       • <b>Cancel:</b> clears context and schedules stat removal.
///     </description>
///   </item>
///   <item>
///     <term><see cref="HealReceiveBehavior"/> — EntityBehavior on every player entity</term>
///     <description>
///       Overrides <c>OnEntityReceiveDamage</c>. When a pending context is present (set by
///       <see cref="HealingInterceptBehavior"/>), scales <c>ref float damage</c> (HP) and
///       mutates <c>damageSource.Duration</c> (HoT speed) before
///       <c>EntityBehaviorHealth</c> applies them. Context is consumed on first read.
///     </description>
///   </item>
/// </list>
///
/// <para><b>Defaults vs traits</b><br/>
/// <see cref="HealingHandsConfig.Defaults"/> applies when the healer has zero matching traits.
/// The moment one or more traits match, defaults are ignored entirely.
/// </para>
/// </summary>
public class HealingHandsModSystem : ModSystem
{
    // ── Public ────────────────────────────────────────────────────────────────

    public HealingHandsConfig Config => _system?.Config ?? _pendingConfig;

    // ── Private ───────────────────────────────────────────────────────────────

    private ICoreServerAPI? _serverApi;
    private HealingHandsSystem? _system;
    private HealingHandsConfig _pendingConfig = new();

    // Per-player behavior instances so we can remove the correct instance on leave.
    private readonly Dictionary<string, HealReceiveBehavior> _receiveBehaviors = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void Start(ICoreAPI api)
    {
        // Register on both sides so class names survive item-type / entity network packets.
        // Client instances of both behaviors are inert (all logic guards EnumAppSide.Server).
        api.RegisterCollectibleBehaviorClass("HealingInterceptBehavior", typeof(HealingInterceptBehavior));
        api.RegisterEntityBehaviorClass("healinghands:healreceive",      typeof(HealReceiveBehavior));

        if (api is not ICoreServerAPI sapi) return;

        _serverApi     = sapi;
        _pendingConfig = LoadConfig(sapi);
        _system        = new HealingHandsSystem(sapi, _pendingConfig);
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
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

    internal void ReloadConfig(ICoreServerAPI api)
    {
        _pendingConfig = LoadConfig(api);
        // Swapping _system propagates the new config to all existing behavior closures
        // on the next heal, since they capture `this` and read _system at call time.
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

    private bool TryInjectInterceptBehavior(CollectibleObject? col)
    {
        if (col == null) return false;
        if (col.GetCollectibleBehavior<HealingInterceptBehavior>(withInheritance: false) != null) return false;

        // BehaviorHealingItem is a public type in VS 1.21 exposing a typed Config.
        BehaviorHealingItem? healBehavior =
            col.GetCollectibleBehavior<BehaviorHealingItem>(withInheritance: true);
        if (healBehavior == null || healBehavior.Config.Health <= 0f) return false;

        HealingInterceptBehavior intercept = new(
            col,
            (healer, target) => _system!.ComputeModifier(healer, target),
            _serverApi!.Logger);

        // PREPEND at index 0 so our Start fires before BehaviorHealingItem sets
        // PreventSubsequent, and our Stop fires before vanilla's Stop calls ReceiveDamage.
        CollectibleBehavior[] old     = col.CollectibleBehaviors;
        CollectibleBehavior[] updated = new CollectibleBehavior[old.Length + 1];
        updated[0] = intercept;
        Array.Copy(old, 0, updated, 1, old.Length);
        col.CollectibleBehaviors = updated;
        return true;
    }
}
