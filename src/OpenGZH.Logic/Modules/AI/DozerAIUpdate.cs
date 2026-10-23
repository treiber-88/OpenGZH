using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// Construction vehicles (and GLA workers through <see cref="WorkerAIUpdate"/>): drive to a building
/// site and build it, repair damaged buildings, and when left idle for BoredTime, go and repair
/// damaged friendly buildings within BoredRange on their own.
/// </summary>
public class DozerAIUpdate : AIUpdateInterface
{
    public enum DozerTask { None, Build, Repair }

    public DozerTask Task { get; private set; }
    public uint TaskTargetId { get; private set; }

    float repairPercentPerFrame;
    int boredFrames;
    float boredRange;
    int idleSinceFrame;

    protected override void Initialize()
    {
        base.Initialize();
        // Written as a percentage ("2%"), which the value parser turns into 0.02.
        repairPercentPerFrame = FloatSetting("RepairHealthPercentPerSecond", 0.02f) / LogicTime.FramesPerSecond;
        boredFrames = FramesSetting("BoredTime", 5000);
        boredRange = FloatSetting("BoredRange", 150);
    }

    /// <summary>Starts (or resumes) building a structure that is under construction.</summary>
    public void Construct(GameObject structure)
    {
        Task = DozerTask.Build;
        TaskTargetId = structure.Id;
        structure.BuilderId = Owner.Id;
        MoveTo(ApproachPoint(structure), CommandSource.Dozer);
    }

    public void Repair(GameObject structure)
    {
        // (The original ignores a second repair of a tower on the bridge already being repaired.)
        if (Task == DozerTask.Repair && Game.FindObject(TaskTargetId)?.FindModule<Behaviors.BridgeTowerBehavior>() is { } current
            && structure.FindModule<Behaviors.BridgeTowerBehavior>() is { } next && current.BridgeId == next.BridgeId)
            return;
        if (structure.Health >= structure.MaxHealth || structure.IsUnderConstruction)
            return;
        Task = DozerTask.Repair;
        TaskTargetId = structure.Id;
        MoveTo(ApproachPoint(structure), CommandSource.Dozer);
    }

    /// <summary>Drops all building and repair jobs (a hijacked dozer).</summary>
    public void CancelAllTasks() => EndTask();

    public override void Stop(CommandSource source = CommandSource.Player)
    {
        if (source != CommandSource.Dozer)
            EndTask();
        base.Stop(source);
    }

    public override void MoveTo(System.Numerics.Vector3 destination, CommandSource source = CommandSource.Player)
    {
        if (source != CommandSource.Dozer)
            EndTask();
        base.MoveTo(destination, source);
    }

    /// <summary>A point beside the building on the dozer's side.</summary>
    System.Numerics.Vector3 ApproachPoint(GameObject structure)
    {
        float angle = MathUtil.AngleTo(structure.Position, Owner.Position);
        var point = structure.Position + MathUtil.Forward(angle) * (structure.Radius + Owner.Radius + 2);
        return Game.Pathfinder.NearestPassable(point, Surfaces) ?? point;
    }

    bool IsNextTo(GameObject structure) =>
        MathUtil.Distance2D(Owner.Position, structure.Position) <= structure.Radius + Owner.Radius + 15;

    public override int Update()
    {
        var target = Game.FindObject(TaskTargetId);
        if (Task != DozerTask.None && (target == null || target.IsEffectivelyDead && !target.IsKindOf("BRIDGE_TOWER")))
            EndTask();

        if (target != null && Task != DozerTask.None && IsNextTo(target))
        {
            if (!IsIdle)
                base.Stop(CommandSource.Dozer);
            Work(target);
            return 0;
        }

        if (Task != DozerTask.None && IsIdle && target != null)
            MoveTo(ApproachPoint(target), CommandSource.Dozer);

        int sleep = base.Update();
        if (IsIdle && Task == DozerTask.None)
            LookForBoredWork();
        else
            idleSinceFrame = Frame;
        return sleep;
    }

    void Work(GameObject target)
    {
        Owner.SetOrientation(MathUtil.TurnTowards(Owner.Orientation, MathUtil.AngleTo(Owner.Position, target.Position), 0.2f));
        Owner.SetCondition(Condition.ActivelyConstructing);

        if (Task == DozerTask.Build)
        {
            if (!target.IsUnderConstruction)
            {
                EndTask();
                return;
            }
            target.SetCondition(Condition.ActivelyBeingConstructed);
            float frames = Math.Max(1, LogicTime.FramesFromSeconds(target.Template.BuildTimeSeconds / Math.Max(0.01f, Game.Catalog.GameData.StructureBuildSpeedRate)));
            Game.Building.AddConstructionProgress(target, 1f / frames);
            if (!target.IsUnderConstruction)
                EndTask();
        }
        else if (Task == DozerTask.Repair)
        {
            target.SetStatus("UNDERGOING_REPAIR");
            // A bridge is repaired from its towers, once its scaffolding is up (the original's DozerActionDoActionState).
            var bridge = target.FindModule<Behaviors.BridgeTowerBehavior>() is { } tower ? Game.FindObject(tower.BridgeId)?.FindModule<Behaviors.BridgeBehavior>() : null;
            bridge?.CreateScaffolding();
            if (bridge is { IsScaffoldInMotion: true })
                return;
            target.Body?.AttemptHealing(target.MaxHealth * repairPercentPerFrame, Owner.Id);
            if (target.Health >= target.MaxHealth)
                EndTask();
        }
    }

    void EndTask()
    {
        if (Game.FindObject(TaskTargetId) is { } target)
        {
            target.SetCondition(Condition.ActivelyBeingConstructed, false);
            target.SetStatus("UNDERGOING_REPAIR", false);
            if (target.FindModule<Behaviors.BridgeTowerBehavior>() is { } tower)
                Game.FindObject(tower.BridgeId)?.FindModule<Behaviors.BridgeBehavior>()?.RemoveScaffolding();
        }
        Owner.SetCondition(Condition.ActivelyConstructing, false);
        Task = DozerTask.None;
        TaskTargetId = 0;
        idleSinceFrame = Frame;
    }

    void LookForBoredWork()
    {
        if (boredFrames <= 0 || Frame - idleSinceFrame < boredFrames)
            return;
        idleSinceFrame = Frame;
        var damaged = Game.ObjectsInRadius(Owner.Position, boredRange)
            .Where(o => o.Owner == Owner.Owner && o.IsKindOf(Kind.Structure) && !o.IsUnderConstruction && o.Health < o.MaxHealth && !o.IsEffectivelyDead)
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, Owner.Position))
            .FirstOrDefault();
        if (damaged != null)
            Repair(damaged);
    }
}

/// <summary>
/// GLA workers (the original's WorkerAIUpdate): a dozer brain for building and repairing and a supply
/// truck brain, switching to the truck brain when told to dock (or forced to gather) and back once it
/// is idle and not wanted for supplies. With Worker Shoes they earn UpgradedSupplyBoost extra per trip.
/// </summary>
public sealed class WorkerAIUpdate : DozerAIUpdate
{
    public Supply.SupplyGatherer Gatherer { get; private set; } = null!;
    bool actingAsSupplyTruck;

    protected override void Initialize()
    {
        base.Initialize();
        int boost = IntSetting("UpgradedSupplyBoost");
        Gatherer = new Supply.SupplyGatherer(this, new Supply.ModuleSettings(this),
            () => Owner.Owner.HasUpgrade("Upgrade_GLAWorkerShoes") ? boost : 0, onIdleEntered: ExitingSupplyTruckState);
    }

    /// <summary>The truck brain went idle: hand back to the dozer brain.</summary>
    void ExitingSupplyTruckState()
    {
        if (!actingAsSupplyTruck)
            return;
        Stop(CommandSource.AI);
        actingAsSupplyTruck = false;
        Gatherer.Reset();
    }

    protected override bool OnOrder(AIOrder order, Action replay)
    {
        if (order.Source == CommandSource.Player && order.Type == AIOrderType.Dock && order.TargetId != 0)
            Gatherer.PreferredDockId = order.TargetId;
        return base.OnOrder(order, replay);
    }

    public override int Update()
    {
        bool wantsTruck = Gatherer.ForceWanting || IsDocking;
        if (!actingAsSupplyTruck && wantsTruck)
            actingAsSupplyTruck = true;
        else if (actingAsSupplyTruck && !wantsTruck && Gatherer.State == Supply.SupplyGatherer.StateId.Busy)
        {
            actingAsSupplyTruck = false;
            Gatherer.Reset();
        }
        if (actingAsSupplyTruck)
            Gatherer.Update();
        return base.Update();
    }
}

