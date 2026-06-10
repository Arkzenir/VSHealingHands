using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace HealingHands.Commands;

/// <summary>
/// Registers the <c>/healinghands</c> admin command.
///
/// Subcommands:
/// <list type="bullet">
///   <item><c>checktraits &lt;player&gt;</c> — lists active config traits on a player
///   and previews the combined modifier.</item>
///   <item><c>reload</c> — reloads healinghands.json without a server restart.</item>
/// </list>
/// All subcommands require the <c>commandplayer</c> privilege.
/// </summary>
internal static class HealingHandsCommands
{
    internal static void Register(ICoreServerAPI api, HealingHandsModSystem modSystem)
    {
        CommandArgumentParsers p = api.ChatCommands.Parsers;

        api.ChatCommands
            .Create("healinghands")
            .WithDescription("HealingHands admin commands. Subcommands: checktraits, reload.")
            .RequiresPrivilege(Privilege.commandplayer)

            .BeginSubCommand("checktraits")
                .WithDescription(
                    "Lists which trait codes from the config are active on a player and shows\n" +
                    "the combined modifier they would produce when healing another player.\n" +
                    "Usage: /healinghands checktraits <player>")
                .WithArgs(p.OnlinePlayer("player"))
                .HandleWith(args => HandleCheckTraits(args, modSystem))
            .EndSubCommand()

            .BeginSubCommand("reload")
                .WithDescription(
                    "Reloads ModConfig/healinghands.json from disk. Takes effect on the next heal.\n" +
                    "Usage: /healinghands reload")
                .HandleWith(args => HandleReload(args, api, modSystem))
            .EndSubCommand();
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private static TextCommandResult HandleCheckTraits(
        TextCommandCallingArgs args,
        HealingHandsModSystem modSystem)
    {
        IServerPlayer? target = args[0] as IServerPlayer;
        if (target?.Entity == null)
            return TextCommandResult.Error("Player not found or not online.");

        HealingHandsConfig cfg = modSystem.Config;
        var traitsTree = target.Entity.WatchedAttributes.GetTreeAttribute("traits");

        var lines = new List<string>
        {
            $"Trait check for {target.PlayerName} (compounding: {cfg.CompoundingMode}):"
        };

        bool any = false;
        foreach (TraitModifierConfig entry in cfg.TraitModifiers)
        {
            if (traitsTree?.GetBool(entry.TraitCode, false) ?? false)
            {
                lines.Add(
                    $"  ✓ {entry.TraitCode} — " +
                    $"hp×{entry.Values.HpMultiplier:F2}, " +
                    $"healSpeed×{entry.Values.HealSpeedMultiplier:F2}, " +
                    $"applySpeed×{entry.Values.ApplySpeedMultiplier:F2}" +
                    (string.IsNullOrEmpty(entry.Comment) ? "" : $"  ({entry.Comment})"));
                any = true;
            }
        }

        if (!any)
        {
            lines.Add("  (no matching traits — defaults apply)");
            HealingValues d = cfg.Defaults;
            lines.Add(
                $"  Defaults: hp×{d.HpMultiplier:F2}, " +
                $"healSpeed×{d.HealSpeedMultiplier:F2}, " +
                $"applySpeed×{d.ApplySpeedMultiplier:F2}");
        }
        else
        {
            // Show the compounded result (pass null logger — preview only).
            HealModifier combined = HealModifier.Compute(
                target.Entity, cfg, logger: null, healerName: target.PlayerName);

            lines.Add(
                $"  Combined: hp×{combined.HpMultiplier:F2}, " +
                $"healSpeed×{combined.HealSpeedMultiplier:F2}, " +
                $"applySpeed×{combined.ApplySpeedMultiplier:F2}");
        }

        return TextCommandResult.Success(string.Join("\n", lines));
    }

    private static TextCommandResult HandleReload(
        TextCommandCallingArgs args,
        ICoreServerAPI api,
        HealingHandsModSystem modSystem)
    {
        modSystem.ReloadConfig(api);
        return TextCommandResult.Success("[HealingHands] Config reloaded from disk.");
    }
}
