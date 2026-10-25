using System.Numerics;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Modules.Production;

namespace OpenGZH.Logic.Commands;

// Every order a player can give. Commands carry only ids and values (no object references),
// so they can be sent over the network and recorded in replays.

/// <summary>Move units; with <paramref name="AttackMove"/> they fight enemies met on the way.</summary>
public sealed record MoveCommand(int PlayerIndex, uint[] Units, Vector3 Destination, bool AttackMove = false) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        var units = Owned(game, Units).Where(u => u.AI != null && u.IsMobile).ToList();
        var offsets = Formation.Offsets(units, Destination);
        foreach (var (unit, offset) in units.Zip(offsets))
        {
            if (AttackMove)
                unit.AI!.AttackMoveTo(Destination + offset, CommandSource.Player);
            else
                unit.AI!.MoveTo(Destination + offset, CommandSource.Player);
        }
    }
}

public sealed record AttackObjectCommand(int PlayerIndex, uint[] Units, uint Target) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        var target = game.FindObject(Target);
        if (target == null)
            return;
        // As the original's ActionManager: a player can't order attacks on what is hidden in the fog.
        var player = game.Players.All.FirstOrDefault(p => p.Index == PlayerIndex);
        if (player != null && target.Owner != player && game.Shroud.StatusOf(target, player) >= Vision.ObjectShroud.Fogged)
            return;
        foreach (var unit in Owned(game, Units))
            unit.AI?.AttackObject(target, CommandSource.Player);
    }
}

/// <summary>Fire at the ground (Ctrl+click in the original).</summary>
public sealed record ForceAttackPositionCommand(int PlayerIndex, uint[] Units, Vector3 Position) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        foreach (var unit in Owned(game, Units))
            unit.AI?.AttackPosition(Position, CommandSource.Player);
    }
}

public sealed record StopCommand(int PlayerIndex, uint[] Units) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        foreach (var unit in Owned(game, Units))
            unit.AI?.Stop(CommandSource.Player);
    }
}

public sealed record GuardCommand(int PlayerIndex, uint[] Units, Vector3 Position) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        foreach (var unit in Owned(game, Units))
            unit.AI?.Guard(Position, CommandSource.Player);
    }
}

/// <summary>A dozer or worker places a building.</summary>
public sealed record ConstructCommand(int PlayerIndex, uint Builder, string Building, Vector3 Position, float Angle) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        var builder = Owned(game, [Builder]).FirstOrDefault();
        var template = game.Templates.Find(Building);
        var player = Player(game);
        if (builder == null || template == null || player == null)
            return;
        if (!Prerequisites.AreMet(game, player, template.Rules))
            return;
        game.Building.StartConstruction(template, Position, Angle, player, builder);
    }
}

/// <summary>Resume building an unfinished structure, or repair a damaged one.</summary>
public sealed record RepairCommand(int PlayerIndex, uint[] Dozers, uint Structure) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        var structure = game.FindObject(Structure);
        if (structure == null)
            return;
        foreach (var dozer in Owned(game, Dozers))
        {
            var ai = dozer.FindModule<DozerAIUpdate>();
            if (ai == null)
                continue;
            if (structure.IsUnderConstruction)
                ai.Construct(structure);
            else
                ai.Repair(structure);
        }
    }
}

public sealed record ProduceUnitCommand(int PlayerIndex, uint Factory, string Unit) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        var factory = Owned(game, [Factory]).FirstOrDefault()?.FindModule<ProductionUpdate>();
        var template = game.Templates.Find(Unit);
        if (factory == null || template == null)
            return;
        // Only a lack of money is announced; a full queue (or anything else) quietly does nothing.
        string? reason = factory.CanQueueUnit(template);
        if (reason == "NotEnoughMoney")
            game.Events.Eva("InsufficientFunds", Player(game)!);
        else if (reason == null)
            factory.QueueUnit(template);
    }
}

/// <summary>Places a structure finished by a Tiberian Dawn Construction Yard (OpenGZH addition).</summary>
public sealed record PlaceReadyStructureCommand(int PlayerIndex, uint Factory, uint EntryId, Vector3 Position, float Angle) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        var factory = Owned(game, [Factory]).FirstOrDefault()?.FindModule<ProductionUpdate>();
        if (factory?.PlaceReadyStructure(EntryId, Position, Angle) == null && Player(game) is { } player)
            game.Events.Eva("TdCannotDeployHere", player);
    }
}

/// <summary>A Tiberian Dawn MCV unfolds into its Construction Yard where it stands (OpenGZH addition).</summary>
public sealed record DeployCommand(int PlayerIndex, uint[] Units) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        foreach (var unit in Owned(game, Units).ToList())
            unit.FindModule<Modules.Behaviors.TdDeployToStructure>()?.Deploy();
    }
}

public sealed record ProduceUpgradeCommand(int PlayerIndex, uint Building, string Upgrade) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game) =>
        Owned(game, [Building]).FirstOrDefault()?.FindModule<ProductionUpdate>()?.QueueUpgrade(Upgrade);
}

public sealed record CancelProductionCommand(int PlayerIndex, uint Factory, uint EntryId) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game) =>
        Owned(game, [Factory]).FirstOrDefault()?.FindModule<ProductionUpdate>()?.Cancel(EntryId);
}

public sealed record SetRallyPointCommand(int PlayerIndex, uint Factory, Vector3 Position) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        if (Owned(game, [Factory]).FirstOrDefault()?.FindModule<IProductionExit>() is { } exit)
            exit.RallyPoint = Position;
    }
}

/// <summary>Sell a building (refunds GameData SellPercentage of its cost).</summary>
public sealed record SellCommand(int PlayerIndex, uint Structure) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        var structure = Owned(game, [Structure]).FirstOrDefault();
        if (structure != null && !structure.IsUnsellable)
            game.Building.Sell(structure);
    }
}

/// <summary>Spreads a group's destinations out so units don't all aim for the same spot.</summary>
public static class Formation
{
    public static List<Vector3> Offsets(IReadOnlyList<Objects.GameObject> units, Vector3 destination)
    {
        var result = new List<Vector3>(units.Count);
        if (units.Count <= 1)
        {
            result.Add(Vector3.Zero);
            return result;
        }
        int columns = (int)MathF.Ceiling(MathF.Sqrt(units.Count));
        float spacing = units.Max(u => u.Radius) * 2.2f + 4;
        for (int i = 0; i < units.Count; i++)
        {
            int row = i / columns, column = i % columns;
            result.Add(new Vector3((column - (columns - 1) / 2f) * spacing, (row - (columns - 1) / 2f) * spacing, 0));
        }
        return result;
    }
}

/// <summary>
/// Use a special power. With a <paramref name="Source"/> (e.g. the command centre for a general's
/// power) only that object uses it; otherwise every unit in <paramref name="Units"/> that has it.
/// With a target object or a position the power is aimed; with neither it has no target.
/// </summary>
public sealed record DoSpecialPowerCommand(int PlayerIndex, string Power, uint Source, uint[] Units, uint TargetObject = 0,
    Vector3? Position = null, float Angle = float.NaN, uint Options = 0) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        if (game.Catalog.SpecialPower(Power) is not { } power)
            return;
        var users = Source != 0 ? Owned(game, [Source]) : Owned(game, Units);
        var target = TargetObject != 0 ? game.FindObject(TargetObject) : null;
        if (TargetObject != 0 && target == null)
            return;

        foreach (var user in users.ToList())
        {
            if (Modules.SpecialPowers.SpecialPowerRules.ModuleFor(user, power) is not { } module)
                continue;
            if (power.RequiredScience.Length > 0 && !user.Owner.HasScience(power.RequiredScience))
                continue;
            if (target != null)
            {
                if (Modules.SpecialPowers.SpecialPowerRules.CanDoSpecialPowerAtObject(user, target, CommandSource.Player, power))
                    module.DoSpecialPowerAtObject(target, Options);
            }
            else if (Position is { } at)
            {
                if (Modules.SpecialPowers.SpecialPowerRules.CanDoSpecialPowerAtLocation(user, at, CommandSource.Player, power))
                    module.DoSpecialPowerAtLocation(at, Angle, Options);
            }
            else if (Modules.SpecialPowers.SpecialPowerRules.CanDoSpecialPower(user, power, CommandSource.Player))
            {
                module.DoSpecialPower(Options);
            }
            user.UndetectedDefectorUntilFrame = 0; // my secret is out
        }
    }
}

/// <summary>
/// Steers a power that is working (the original's MSG_DO_SPECIAL_POWER_OVERRIDE_DESTINATION): the
/// Particle Cannon's beam drives towards the point.
/// </summary>
public sealed record SpecialPowerOverrideDestinationCommand(int PlayerIndex, string Power, uint Source, Vector3 Position) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        if (game.Catalog.SpecialPower(Power) is not { } power)
            return;
        foreach (var user in Owned(game, [Source]))
            if (Modules.SpecialPowers.SpecialPowerRules.ModuleFor(user, power) is { } module)
                module.SetSpecialPowerOverridableDestination(Position);
    }
}

/// <summary>Units walk to a transport, building or tunnel and get in.</summary>
public sealed record EnterCommand(int PlayerIndex, uint[] Units, uint Container) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        if (game.FindObject(Container) is not { } container)
            return;
        foreach (var unit in Owned(game, Units))
            if (unit.AI != null && unit != container)
                unit.AI.Enter(container, CommandSource.Player);
    }
}

/// <summary>Damaged vehicles drive into a repair bay (war factory, arms dealer, repair pad) to be fixed.</summary>
public sealed record GetRepairedCommand(int PlayerIndex, uint[] Units, uint Depot) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        if (game.FindObject(Depot) is not { } depot)
            return;
        foreach (var unit in Owned(game, Units))
            unit.AI?.GetRepairedAt(depot, CommandSource.Player);
    }
}

/// <summary>China's reactor overcharge on or off.</summary>
public sealed record ToggleOverchargeCommand(int PlayerIndex, uint[] Plants) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        foreach (var plant in Owned(game, Plants))
            plant.FindModule<Modules.Behaviors.OverchargeBehavior>()?.Toggle();
    }
}

/// <summary>Everyone inside the containers gets out.</summary>
public sealed record EvacuateCommand(int PlayerIndex, uint[] Containers) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        foreach (var container in Owned(game, Containers))
        {
            if (container.AI != null)
                container.AI.Evacuate(CommandSource.Player);
            else
                container.Contain?.OrderAllPassengersToExit(CommandSource.Player, instantly: false);
        }
    }
}

/// <summary>One passenger gets out (clicking its portrait on the control bar).</summary>
public sealed record ExitContainerCommand(int PlayerIndex, uint Unit) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        foreach (var unit in Owned(game, [Unit]))
            unit.AI?.ExitContainer(CommandSource.Player);
    }
}

/// <summary>Hackers start hacking the internet where they stand.</summary>
public sealed record HackInternetCommand(int PlayerIndex, uint[] Units) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        foreach (var unit in Owned(game, Units))
            unit.AI?.HackInternet(CommandSource.Player);
    }
}

/// <summary>Transports drop their troops onto a building or a point (Chinook combat drop).</summary>
public sealed record CombatDropCommand(int PlayerIndex, uint[] Units, uint Target, Vector3 Position) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        var target = game.FindObject(Target);
        foreach (var unit in Owned(game, Units))
            unit.AI?.CombatDrop(target, target?.Position ?? Position, CommandSource.Player);
    }
}

/// <summary>Units dock with a building or vehicle (the ferry's ramp, a repair bay).</summary>
public sealed record DockCommand(int PlayerIndex, uint[] Units, uint Dock) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        if (game.FindObject(Dock) is not { } dock)
            return;
        foreach (var unit in Owned(game, Units))
            if (unit != dock)
                unit.AI?.Dock(dock, CommandSource.Player);
    }
}

/// <summary>The ferry sets off along its next route.</summary>
public sealed record ExecuteRailedTransportCommand(int PlayerIndex, uint[] Transports) : GameCommand(PlayerIndex)
{
    public override void Execute(GameLogic game)
    {
        foreach (var transport in Owned(game, Transports))
            (transport.AI as Modules.Contain.RailedTransportAIUpdate)?.ExecuteRailedTransport();
    }
}
