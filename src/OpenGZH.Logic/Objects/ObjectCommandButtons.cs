using System.Numerics;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Modules.SpecialPowers;
using OpenGZH.Logic.Templates;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.Objects;

/// <summary>
/// Using a command button from code (the original's Object::doCommandButton family): scripts and the
/// computer player press buttons for their units the way a player clicks them. Buttons that make no
/// sense without a click (waypoint mode, rally points ...) do nothing, as in the original.
/// </summary>
public static class ObjectCommandButtons
{
    /// <summary>The script flag special powers get (the original's COMMAND_FIRED_BY_SCRIPT option bit).</summary>
    public const uint CommandFiredByScript = 0x00040000;

    /// <summary>The object's command buttons by slot (0-based), with script overrides applied.</summary>
    public static IEnumerable<CommandButton> CommandSetButtons(this GameObject obj)
    {
        var game = obj.Game;
        string setName = obj.CommandSetName;
        foreach (var (slot, button) in CommandSets.Buttons(game.Catalog, setName))
        {
            if (game.ControlBarOverrides.TryGetValue((setName, slot - 1), out var replacement))
            {
                if (replacement != null)
                    yield return replacement;
                continue;
            }
            yield return button;
        }
        // Buttons a script added to empty slots.
        foreach (var ((set, slot), button) in game.ControlBarOverrides)
            if (button != null && set.Equals(setName, StringComparison.OrdinalIgnoreCase)
                && !CommandSets.Buttons(game.Catalog, setName).Any(b => b.Slot - 1 == slot))
                yield return button;
    }

    /// <summary>The object's button with this name, if its command set has it.</summary>
    public static CommandButton? FindCommandButton(this GameObject obj, string name) =>
        obj.CommandSetButtons().FirstOrDefault(b => b.Name.Equals(name, StringComparison.Ordinal));

    static readonly string[] OptionNames =
    [
        "NEED_TARGET_ENEMY_OBJECT", "NEED_TARGET_NEUTRAL_OBJECT", "NEED_TARGET_ALLY_OBJECT", "NEED_TARGET_PRISONER",
        "ALLOW_SHRUBBERY_TARGET", "NEED_TARGET_POS", "NEED_UPGRADE", "NEED_SPECIAL_POWER_SCIENCE", "OK_FOR_MULTI_SELECT",
        "CONTEXTMODE_COMMAND", "CHECK_LIKE", "ALLOW_MINE_TARGET", "ATTACK_OBJECTS_POSITION", "OPTION_ONE", "OPTION_TWO",
        "OPTION_THREE", "NOT_QUEUEABLE", "SINGLE_USE_COMMAND", "COMMAND_FIRED_BY_SCRIPT", "SCRIPT_ONLY", "IGNORES_UNDERPOWERED",
        "USES_MINE_CLEARING_WEAPONSET", "CAN_USE_WAYPOINTS", "MUST_BE_STOPPED",
    ];

    /// <summary>The button's Options as the original's bit mask.</summary>
    public static uint OptionBits(this CommandButton button)
    {
        uint bits = 0;
        for (int i = 0; i < OptionNames.Length; i++)
            if (button.HasOption(OptionNames[i]))
                bits |= 1u << i;
        return bits;
    }

    static uint Options(CommandButton button) => button.OptionBits() | CommandFiredByScript;

    /// <summary>The original's Object::doSpecialPower: a script fires it even without the science.</summary>
    public static void DoSpecialPower(this GameObject obj, SpecialPowerTemplate power, uint options, bool forced)
    {
        if (obj.IsDisabled)
            return;
        if (!forced && !SpecialPowerRules.CanUseSpecialPower(obj, power))
            return;
        SpecialPowerRules.ModuleFor(obj, power)?.DoSpecialPower(options);
    }

    public static void DoSpecialPowerAtObject(this GameObject obj, SpecialPowerTemplate power, GameObject target, uint options, bool forced)
    {
        if (obj.IsDisabled)
            return;
        if (!forced && !SpecialPowerRules.CanUseSpecialPower(obj, power))
            return;
        SpecialPowerRules.ModuleFor(obj, power)?.DoSpecialPowerAtObject(target, options);
    }

    public static void DoSpecialPowerAtLocation(this GameObject obj, SpecialPowerTemplate power, Vector3 location, float angle, uint options, bool forced)
    {
        if (obj.IsDisabled)
            return;
        if (!forced && !SpecialPowerRules.CanUseSpecialPower(obj, power))
            return;
        SpecialPowerRules.ModuleFor(obj, power)?.DoSpecialPowerAtLocation(location, angle, options);
    }

    public static void DoSpecialPowerUsingWaypoints(this GameObject obj, SpecialPowerTemplate power, Waypoint way, uint options, bool forced)
    {
        if (obj.IsDisabled)
            return;
        if (!forced && !SpecialPowerRules.CanUseSpecialPower(obj, power))
            return;
        if (SpecialPowerRules.ModuleFor(obj, power) is not { } module)
            return;
        var route = obj.Game.Waypoints?.RouteFrom(way, n => obj.Game.Random.Int(0, n - 1)) ?? [way.Position];
        module.DoSpecialPowerUsingWaypoints(route, options);
    }

    /// <summary>The original's Object::doCommandButton (no target).</summary>
    public static void DoCommandButton(this GameObject obj, CommandButton button, CommandSource source)
    {
        if (obj.IsDisabled)
            return;
        var ai = obj.AI;
        var game = obj.Game;
        switch (button.CommandType)
        {
            case "SPECIAL_POWER":
                if (game.Catalog.SpecialPower(button.SpecialPower) is { } power)
                {
                    obj.DoSpecialPower(power, Options(button), source == CommandSource.Script);
                    return;
                }
                break;
            case "STOP":
                if (ai != null)
                {
                    ai.Stop(source);
                    return;
                }
                break;
            case "TOGGLE_OVERCHARGE":
                obj.FindModule<Modules.Behaviors.OverchargeBehavior>()?.Toggle();
                return;
            case "SWITCH_WEAPON":
                obj.Weapons.LockSlot(button.Slot);
                return;
            case "FIRE_WEAPON":
                if (ai != null)
                {
                    if (!button.NeedsObjectTarget && !button.NeedsTargetPosition)
                    {
                        obj.Weapons.LockSlot(button.Slot);
                        // (The original attacks a null position here; there is nothing to aim at.)
                        ai.AttackPosition(obj.Position, source, button.MaxShotsToFire);
                    }
                    return;
                }
                break;
            case "OBJECT_UPGRADE":
            case "PLAYER_UPGRADE":
            {
                var upgrade = game.Catalog.Upgrade(button.Upgrade);
                if (upgrade == null)
                    break;
                bool isObjectUpgrade = upgrade.GetString("Type", "PLAYER").Equals("OBJECT", StringComparison.OrdinalIgnoreCase);
                if (isObjectUpgrade && (obj.HasUpgrade(button.Upgrade) || !obj.IsAffectedByUpgrade(button.Upgrade)))
                    break;
                if (obj.FindModule<Modules.Production.ProductionUpdate>() is not { } production)
                    break;
                production.QueueUpgrade(button.Upgrade);
                return;
            }
            case "UNIT_BUILD":
            case "DOZER_CONSTRUCT":
            {
                var template = game.Templates.Find(button.Object);
                if (obj.FindModule<Modules.Production.ProductionUpdate>() is { } production && template != null)
                {
                    production.QueueUnit(template);
                    return;
                }
                break;
            }
            case "HACK_INTERNET":
                if (ai != null)
                {
                    ai.HackInternet(source);
                    return;
                }
                break;
            case "SELL":
                game.Building.Sell(obj);
                return;
        }
        Log.Warning($"Script doCommandButton for button {button.Name} not implemented. Doing nothing.");
    }

    /// <summary>The original's Object::doCommandButtonAtObject.</summary>
    public static void DoCommandButtonAtObject(this GameObject obj, CommandButton button, GameObject? target, CommandSource source)
    {
        if (obj.IsDisabled)
            return;
        var ai = obj.AI;
        var game = obj.Game;
        switch (button.CommandType)
        {
            case "COMBATDROP":
                if (target != null)
                    ai?.CombatDrop(target, target.Position, source);
                return;
            case "SPECIAL_POWER":
                if (game.Catalog.SpecialPower(button.SpecialPower) is { } power && target != null)
                    obj.DoSpecialPowerAtObject(power, target, Options(button), source == CommandSource.Script);
                return;
            case "STOP":
                ai?.Stop(source);
                return;
            case "FIRE_WEAPON":
                if (ai != null)
                {
                    if (button.NeedsObjectTarget)
                    {
                        if (target == null || !button.IsValidObjectTarget(obj, target))
                            break;
                        obj.Weapons.LockSlot(button.Slot);
                        if (button.HasOption("ATTACK_OBJECTS_POSITION"))
                            ai.AttackPosition(target.Position, source, button.MaxShotsToFire);
                        else
                            ai.AttackObject(target, source, button.MaxShotsToFire);
                    }
                    return;
                }
                break;
            case "HIJACK_VEHICLE":
            case "CONVERT_TO_CARBOMB":
            case "SABOTAGE_BUILDING":
                if (target != null)
                    ai?.Enter(target, source);
                return;
        }
        Log.Warning($"Script doCommandButtonAtObject for button {button.Name} not implemented. Doing nothing.");
    }

    /// <summary>The original's Object::doCommandButtonAtPosition.</summary>
    public static void DoCommandButtonAtPosition(this GameObject obj, CommandButton button, Vector3 position, CommandSource source)
    {
        if (obj.IsDisabled)
            return;
        var ai = obj.AI;
        var game = obj.Game;
        switch (button.CommandType)
        {
            case "SPECIAL_POWER":
                if (game.Catalog.SpecialPower(button.SpecialPower) is { } power)
                {
                    obj.DoSpecialPowerAtLocation(power, position, InvalidAngle, Options(button), source == CommandSource.Script);
                    return;
                }
                break;
            case "ATTACK_MOVE":
                if (ai != null)
                {
                    ai.AttackMoveTo(position, source);
                    return;
                }
                break;
            case "STOP":
                if (ai != null)
                {
                    ai.Stop(source);
                    return;
                }
                break;
            case "DOZER_CONSTRUCT":
                if (game.Templates.Find(button.Object) is { } building)
                    game.Building.BuildObjectNow(obj, building, position, 0, obj.Owner);
                return;
            case "FIRE_WEAPON":
                if (ai != null)
                {
                    if (button.NeedsTargetPosition)
                    {
                        obj.Weapons.LockSlot(button.Slot);
                        ai.AttackPosition(position, source, button.MaxShotsToFire);
                    }
                    return;
                }
                break;
        }
        Log.Warning($"Script doCommandButtonAtPosition for button {button.Name} not implemented. Doing nothing.");
    }

    /// <summary>The original's Object::doCommandButtonUsingWaypoints (only special powers that allow it).</summary>
    public static void DoCommandButtonUsingWaypoints(this GameObject obj, CommandButton button, Waypoint way, CommandSource source)
    {
        if (obj.IsDisabled)
            return;
        if (!button.HasOption("CAN_USE_WAYPOINTS"))
        {
            Log.Warning($"Script doCommandButtonUsingWaypoints for button {button.Name} lacks CAN_USE_WAYPOINTS option. Doing nothing.");
            return;
        }
        if (button.CommandType == "SPECIAL_POWER" && obj.Game.Catalog.SpecialPower(button.SpecialPower) is { } power)
        {
            obj.DoSpecialPowerUsingWaypoints(power, way, Options(button), source == CommandSource.Script);
            return;
        }
        Log.Warning($"Script doCommandButtonUsingWaypoints for button {button.Name} not implemented. Doing nothing.");
    }

    /// <summary>The original's INVALID_ANGLE: the power picks its own facing.</summary>
    public const float InvalidAngle = -100f;

    /// <summary>The original's CommandButton::isValidObjectTarget (by the two objects' relationship).</summary>
    public static bool IsValidObjectTarget(this CommandButton button, GameObject? source, GameObject? target) =>
        source != null && target != null && button.IsValidRelationshipTarget(source.RelationshipTo(target));

    /// <summary>The original's CommandButton::isValidToUseOn: can the source use this button on the target or spot now?</summary>
    public static bool IsValidToUseOn(this CommandButton button, GameObject source, GameObject? target, Vector3? location, CommandSource commandSource)
    {
        var game = source.Game;
        if (button.Upgrade.Length > 0 && game.Catalog.Upgrade(button.Upgrade) != null)
        {
            if (source.FindModule<Modules.Production.ProductionUpdate>() is { } production)
            {
                if (production.Queue.Any(e => e.Upgrade != null))
                    return false;
                return source.IsAffectedByUpgrade(button.Upgrade) && !source.HasUpgrade(button.Upgrade);
            }
            return false;
        }
        if (button.NeedsObjectTarget && target == null)
            return false;
        Vector3 pos = location ?? default;
        if (button.NeedsTargetPosition && location == null)
        {
            if (target != null)
                pos = target.Position;
            else
                return false;
        }
        var power = game.Catalog.SpecialPower(button.SpecialPower);
        if (power == null)
            return false;
        if (button.NeedsObjectTarget)
            return SpecialPowerRules.CanDoSpecialPowerAtObject(source, target!, commandSource, power, checkSource: false);
        if (button.NeedsTargetPosition)
            return SpecialPowerRules.CanDoSpecialPowerAtLocation(source, pos, commandSource, power, checkSource: false);
        return SpecialPowerRules.CanDoSpecialPower(source, power, commandSource, checkSource: false);
    }

    /// <summary>The original's CommandButton::isReady: the power is charged, or the upgrade can be bought.</summary>
    public static bool IsReady(this CommandButton button, GameObject source)
    {
        var game = source.Game;
        if (game.Catalog.SpecialPower(button.SpecialPower) is { } power && SpecialPowerRules.ModuleFor(source, power) is { } module && module.PercentReady >= 1f)
            return true;
        if (button.Upgrade.Length > 0 && source.IsAffectedByUpgrade(button.Upgrade) && !source.HasUpgrade(button.Upgrade))
            return true;
        return false;
    }
}
