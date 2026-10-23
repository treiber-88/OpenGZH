using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Production;

/// <summary>
/// The standard factory exit: units appear at UnitCreatePoint and drive to NaturalRallyPoint
/// (both relative to the building's facing), or to the player's rally point if one is set.
/// </summary>
public class DefaultProductionExitUpdate : Module, IProductionExit
{
    protected Vector3 CreatePoint;
    protected Vector3 NaturalRallyPoint;

    public Vector3? RallyPoint { get; set; }

    protected override void Initialize()
    {
        CreatePoint = RuleValue.Coord(Setting("UnitCreatePoint"));
        NaturalRallyPoint = RuleValue.Coord(Setting("NaturalRallyPoint"), CreatePoint + new Vector3(30, 0, 0));
    }

    public virtual bool CanExitNow => true;

    protected Vector3 ToWorld(Vector3 local) =>
        Owner.Position + Vector3.Transform(local with { Z = 0 }, Matrix4x4.CreateRotationZ(Owner.Orientation));

    public virtual void Exit(GameObject unit)
    {
        var start = ToWorld(CreatePoint);
        unit.SetOrientation(Owner.Orientation);
        if (unit.AI != null)
            unit.AI.Teleport(start);
        else
            unit.SetPosition(start with { Z = Game.Terrain.LayerHeight(start.X, start.Y) });

        var destination = RallyPoint ?? ToWorld(NaturalRallyPoint);
        unit.AI?.MoveTo(destination, CommandSource.AI);
    }
}

/// <summary>Like the default exit, but units leave one at a time with ExitDelay between them (e.g. barracks).</summary>
public sealed class QueueProductionExitUpdate : DefaultProductionExitUpdate
{
    int exitDelay;
    int nextExitFrame;
    int burstRemaining;

    protected override void Initialize()
    {
        base.Initialize();
        exitDelay = FramesSetting("ExitDelay");
        burstRemaining = IntSetting("InitialBurst");
    }

    public override bool CanExitNow => Frame >= nextExitFrame;

    public override void Exit(GameObject unit)
    {
        base.Exit(unit);
        if (burstRemaining > 0)
            burstRemaining--;
        else
            nextExitFrame = Frame + exitDelay;
    }
}

/// <summary>Supply centers' exit for harvesters (can grant brief stealth to the new truck).</summary>
public sealed class SupplyCenterProductionExitUpdate : DefaultProductionExitUpdate
{
    /// <summary>A new harvester heads straight off to gather (the original forces it into wanting).</summary>
    public override void Exit(GameObject unit)
    {
        base.Exit(unit);
        if (Supply.SupplyGatherer.Of(unit) is { } gatherer)
            gatherer.ForceWanting = true;
        int stealthFrames = FramesSetting("GrantTemporaryStealth");
        if (stealthFrames > 0 && unit.FindModule<Stealth.StealthUpdate>() is { } stealth)
            stealth.ReceiveGrant(true, stealthFrames);
    }
}

/// <summary>Units appear at named bones of the building's model (e.g. GLA tunnels, spawn points).</summary>
public sealed class SpawnPointProductionExitUpdate : DefaultProductionExitUpdate
{
}
