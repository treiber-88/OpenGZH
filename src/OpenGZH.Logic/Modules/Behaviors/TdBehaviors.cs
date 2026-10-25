using System.Numerics;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// OpenGZH addition for Tiberian Dawn factions: a Mobile Construction Vehicle unfolds into its
/// Construction Yard (Structure:) where it stands, if the yard fits there (TD's UnitClass::Try_To_Deploy).
/// The yard keeps the MCV's health fraction, and the MCV is gone.
/// </summary>
public sealed class TdDeployToStructure : Module
{
    /// <summary>The structure the MCV becomes.</summary>
    public string StructureName => Setting("Structure");

    /// <summary>Whether the yard would fit where the MCV stands.</summary>
    public bool CanDeploy()
    {
        if (Owner.IsEffectivelyDead || Game.Templates.Find(StructureName) is not { } yard)
            return false;
        return Game.Building.CheckLocation(yard, Owner.Position, 0, Owner.Owner, Owner) == Construction.BuildLocation.Ok;
    }

    /// <summary>Unfolds into the Construction Yard (false if it doesn't fit; TD says "Cannot deploy here").</summary>
    public bool Deploy()
    {
        if (!CanDeploy() || Game.Templates.Find(StructureName) is not { } yard)
        {
            Game.Events.Eva("TdCannotDeployHere", Owner.Owner);
            return false;
        }
        var player = Owner.Owner;
        var position = Owner.Position with { Z = Game.Terrain.GroundHeight(Owner.Position) };
        float health = Owner.MaxHealth > 0 ? Owner.Health / Owner.MaxHealth : 1;
        var team = Owner.Team;
        Game.Building.ClearSceneryFor(yard, position, 0, Owner);
        Game.DestroyObject(Owner);
        var structure = Game.CreateObject(yard, player, position, 0, team);
        Game.TreatAsBuilt(structure);
        if (structure.Body != null && health < 1)
            structure.Body.SetHealth(structure.MaxHealth * health);
        player.Score.AddObjectBuilt(structure);
        player.AI?.OnStructureProduced(null, structure);
        Game.Events.Eva("TdDeploy", player);
        return true;
    }
}

/// <summary>
/// OpenGZH addition for Tiberian Dawn factions: BuildingClass::Grand_Opening's free unit. A finished
/// refinery puts a harvester (Unit:) next to its south-west corner; if that spot is taken, somewhere
/// nearby. (A captured building is already built, so it gives nothing.)
/// </summary>
public sealed class TdFreeUnitOnBuilt : Module, IUpdateModule
{
    bool done;

    public int Update()
    {
        if (done)
            return LogicTime.SleepForever;
        if (Owner.IsUnderConstruction)
            return 1;
        done = true;
        if (Owner.Owner.IsNeutral || Game.Templates.Find(Setting("Unit")) is not { } unit)
            return LogicTime.SleepForever;
        var corner = Owner.Position + new Vector3(-Owner.Geometry.MajorRadius - 18, -Owner.Geometry.MinorRadius - 18, 0);
        var at = Spatial.PositionFinder.FindPositionAround(Game, corner, 0, 150) ?? corner;
        at.Z = Game.Terrain.GroundHeight(at);
        var obj = Game.CreateObject(unit, Owner.Owner, at, MathF.PI * 1.25f, Owner.Team);
        Game.TreatAsBuilt(obj);
        Owner.Owner.OnUnitCreated(Owner, obj);
        if (obj.AI is Supply.SupplyTruckAIUpdate truck)
            truck.Gatherer.ForceWanting = true; // off to harvest, as a harvester leaving a factory
        return LogicTime.SleepForever;
    }

}
