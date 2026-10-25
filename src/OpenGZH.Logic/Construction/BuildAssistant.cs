using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Construction;

/// <summary>Why a building can't go somewhere (matches the original's LegalBuildCode).</summary>
public enum BuildLocation
{
    Ok, RestrictedTerrain, NotFlatEnough, ObjectsInTheWay, TooCloseToSupplies, NoClearPath, Shroud, NeedsShoreline,
    TooFarFromCommandCenter,
}

/// <summary>
/// Where buildings may be placed, and the start of construction. Follows the original rules:
/// on the map, not on cliffs or water, flat enough (GameData AllowedHeightVariationForBuilding),
/// clear of other buildings, and away from supplies for buildings marked CANNOT_BUILD_NEAR_SUPPLIES.
///
/// OpenGZH naval addition: objects with KindOf SHIPYARD must instead stand wholly on water, next to
/// the shore (dry ground at the edge for the dozer to build from), with the ship exit point on water.
/// </summary>
public sealed class BuildAssistant
{
    readonly GameLogic game;

    public BuildAssistant(GameLogic game) => this.game = game;

    float AllowedHeightVariation => RuleValue.Float(game.Catalog.GameData.Get("AllowedHeightVariationForBuilding"), 10);

    public BuildLocation CheckLocation(ObjectTemplate building, Vector3 position, float angle, Player player, GameObject? builder = null)
    {
        var terrain = game.Terrain;
        float margin = game.Catalog.GameData.MinDistanceFromMapEdgeForStructures;
        if (position.X < margin || position.Y < margin || position.X > terrain.WorldWidth - margin || position.Y > terrain.WorldHeight - margin)
            return BuildLocation.RestrictedTerrain;

        bool tdStructure = RuleValue.Bool(building.Rules.GetString("TdBuiltByYard"));
        if (tdStructure ? !IsNextToOwnBase(building, position, angle, player) : !IsWithinCommandCenterRange(building, position, player))
            return BuildLocation.TooFarFromCommandCenter;

        bool shipyard = building.Is("SHIPYARD");
        int landCells = 0, waterCells = 0, totalCells = 0;
        float lowest = float.MaxValue, highest = float.MinValue;

        foreach (var (x, y) in game.Pathfinder.FootprintCells(position, angle, building.GeometryName, building.GeometryMajorRadius, building.GeometryMinorRadius))
        {
            totalCells++;
            bool water = terrain.IsWaterCell(x, y);

            if (shipyard)
            {
                // A shipyard stands wholly on water.
                if (!water)
                    return BuildLocation.NeedsShoreline;
                waterCells++;
                continue;
            }
            if (water || terrain.IsCliffCell(x, y))
                return BuildLocation.RestrictedTerrain;
            landCells++;
            float height = terrain.CellCenter(x, y).Z;
            lowest = Math.Min(lowest, height);
            highest = Math.Max(highest, height);
        }

        if (shipyard && (waterCells == 0 || !IsShipExitOnWater(building, position, angle) || !HasDozerFooting(building, position)))
            return BuildLocation.NeedsShoreline;

        if (landCells > 0 && highest - lowest > AllowedHeightVariation)
            return BuildLocation.NotFlatEnough;

        // Other buildings (and immobile objects) may not overlap; units will move out of the way.
        float radius = Math.Max(building.GeometryMajorRadius, building.GeometryMinorRadius);
        foreach (var other in game.ObjectsInRadius(position, tdStructure ? radius + 150 : radius))
        {
            if (other == builder || other.IsEffectivelyDead)
                continue;
            bool blocking = tdStructure ? TdBlocks(other, deploying: builder?.FindModule<Modules.Behaviors.TdDeployToStructure>() != null)
                : other.IsKindOf(Kind.Structure) || other.IsKindOf(Kind.Immobile) && !other.IsKindOf(Kind.Shrubbery) && !other.IsKindOf("CLEARED_BY_BUILD");
            if (blocking && (tdStructure ? TdOverlaps(building, position, angle, other) : Overlaps(building, position, angle, other)))
                return BuildLocation.ObjectsInTheWay;
        }

        if (building.Is("CANNOT_BUILD_NEAR_SUPPLIES"))
        {
            float border = game.Catalog.GameData.SupplyBuildBorder;
            foreach (var supply in game.ObjectsInRadius(position, radius + border * 2))
                if (supply.IsKindOf(Kind.SupplySource) && MathUtil.Distance2D(supply.Position, position) < radius + supply.Radius + border)
                    return BuildLocation.TooCloseToSupplies;
        }

        return BuildLocation.Ok;
    }

    /// <summary>
    /// OpenGZH addition (GameData MaxBuildDistanceFromCommandCenter): a human player builds only near
    /// one of their Command Centers. With none left, a new Command Center may go anywhere. Shipyards
    /// are exempt: they can only go where the map has a usable shore, which may be far from any base.
    /// </summary>
    bool IsWithinCommandCenterRange(ObjectTemplate building, Vector3 position, Player player)
    {
        float limit = game.Catalog.GameData.MaxBuildDistanceFromCommandCenter;
        if (limit <= 0 || !player.IsHuman || building.Is("SHIPYARD"))
            return true;
        bool anyCommandCenter = false;
        foreach (var obj in game.ObjectsOwnedBy(player))
        {
            if (obj.IsEffectivelyDead || !obj.IsKindOf("COMMANDCENTER"))
                continue;
            anyCommandCenter = true;
            if (MathUtil.Distance2D(obj.Position, position) <= limit)
                return true;
        }
        return !anyCommandCenter && building.Is("COMMANDCENTER");
    }

    /// <summary>
    /// Tiberian Dawn's adjacency rule (BuildingTypeClass::Legal_Placement / MapClass::Passes_Proximity_Check):
    /// a structure must touch, within one TD cell, a structure the player already owns. TD measures this on
    /// the footprint plus a one-cell border, so here: the boxes (grown by one TD cell = 36 units) overlap.
    /// A Construction Yard (COMMANDCENTER) comes from an MCV and is exempt.
    /// </summary>
    bool IsNextToOwnBase(ObjectTemplate building, Vector3 position, float angle, Player player)
    {
        if (building.Is("COMMANDCENTER"))
            return true;
        const float TdCell = 36;
        var mine = TdFootprint(building, position, angle);
        foreach (var obj in game.ObjectsInRadius(position, Math.Max(mine.HalfX, mine.HalfY) + TdCell + 200))
        {
            if (obj.Owner != player || obj.IsEffectivelyDead || !obj.IsKindOf(Kind.Structure))
                continue;
            var theirs = TdFootprint(obj.Template, obj.Position, obj.Orientation, obj.Geometry);
            if (RectsOverlap(mine.Center, angle, mine.HalfX + TdCell, mine.HalfY + TdCell, theirs.Center, obj.Orientation, theirs.HalfX, theirs.HalfY))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The rectangle an object keeps clear of TD structures: a TD structure's TdFootprint (its cells
    /// with the bib, which other buildings may not cover though units drive over it), else its box.
    /// </summary>
    static (Vector3 Center, float HalfX, float HalfY) TdFootprint(ObjectTemplate template, Vector3 position, float angle, Core.Geometry? geometry = null)
    {
        var words = RuleValue.Words(template.Rules.GetString("TdFootprint"));
        if (words.Length == 4)
        {
            var offset = Vector3.Transform(new Vector3(RuleValue.Float(words[0]), RuleValue.Float(words[1]), 0), Matrix4x4.CreateRotationZ(angle));
            return (position + offset, RuleValue.Float(words[2]), RuleValue.Float(words[3]));
        }
        var g = geometry ?? template.Geometry;
        return (position, g.MajorRadius, g.MinorRadius);
    }

    /// <summary>
    /// What stands in a TD structure's way: buildings, and solid scenery that can be hit (it has a body).
    /// Things with no body (sound emitters, markers) never do. An MCV unfolding clears the scenery under
    /// its yard instead (see <see cref="ClearSceneryFor"/>): Zero Hour's start spots are laid out for a
    /// Command Center, and trees or props near one would otherwise keep the MCV from deploying at all.
    /// </summary>
    public static bool TdBlocks(GameObject other, bool deploying)
    {
        if (other.IsKindOf(Kind.Structure))
            return true;
        if (!other.IsKindOf(Kind.Immobile) || other.Body == null)
            return false;
        if (other.IsKindOf(Kind.Shrubbery) || other.IsKindOf("CLEARED_BY_BUILD"))
            return false;
        return !deploying;
    }

    /// <summary>Removes the scenery (immobile, not a building, with a body) under a structure that is going up.</summary>
    public void ClearSceneryFor(ObjectTemplate building, Vector3 position, float angle, GameObject? except = null)
    {
        var area = TdFootprint(building, position, angle);
        foreach (var other in game.ObjectsInRadius(area.Center, Math.Max(area.HalfX, area.HalfY) + 40).ToList())
        {
            if (other == except || other.IsEffectivelyDead || other.IsKindOf(Kind.Structure) || !other.IsKindOf(Kind.Immobile) || other.Body == null)
                continue;
            if (RectsOverlap(area.Center, angle, area.HalfX, area.HalfY, other.Position, other.Orientation, other.Radius, other.Radius))
                game.DestroyObject(other);
        }
    }

    /// <summary>Overlap for a TD structure being placed: footprints against other structures, its box against everything else.</summary>
    static bool TdOverlaps(ObjectTemplate building, Vector3 position, float angle, GameObject other)
    {
        if (other.IsKindOf(Kind.Structure))
        {
            var mine = TdFootprint(building, position, angle);
            var theirs = TdFootprint(other.Template, other.Position, other.Orientation, other.Geometry);
            return RectsOverlap(mine.Center, angle, mine.HalfX - 0.5f, mine.HalfY - 0.5f, theirs.Center, other.Orientation, theirs.HalfX - 0.5f, theirs.HalfY - 0.5f);
        }
        if (other.Geometry.Type == Core.GeometryType.Box)
            return RectsOverlap(position, angle, building.GeometryMajorRadius - 0.5f, building.GeometryMinorRadius - 0.5f,
                other.Position, other.Orientation, other.Geometry.MajorRadius - 0.5f, other.Geometry.MinorRadius - 0.5f);
        return Overlaps(building, position, angle, other);
    }

    /// <summary>The ground, or the water surface for a building on water (a shipyard floats).</summary>
    float SurfaceHeight(Vector3 position) =>
        game.Terrain.IsUnderwater(position.X, position.Y, out float water) ? Math.Max(water, game.Terrain.GroundHeight(position)) : game.Terrain.GroundHeight(position);

    /// <summary>
    /// Drivable ground near a shipyard where a dozer can stand to build it: within the dozer's working
    /// reach (DozerAIUpdate.IsNextTo: the building's radius plus the dozer's plus 15, so at least radius + 25).
    /// </summary>
    bool HasDozerFooting(ObjectTemplate building, Vector3 position)
    {
        float radius = Math.Max(building.GeometryMajorRadius, building.GeometryMinorRadius);
        float inner = Math.Min(building.GeometryMajorRadius, building.GeometryMinorRadius);
        for (float r = inner + 5; r <= radius + 25; r += 5)
            for (int i = 0; i < 32; i++)
            {
                float angle = i * MathF.Tau / 32;
                var p = position + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * r;
                if (game.Pathfinder.IsPassable(p, Locomotion.Surfaces.Ground))
                    return true;
            }
        return false;
    }

    /// <summary>A shipyard's exit point (its production exit's UnitCreatePoint) must be in water.</summary>
    bool IsShipExitOnWater(ObjectTemplate building, Vector3 position, float angle)
    {
        var exit = building.Modules.FirstOrDefault(m => m.ClassName.EndsWith("ProductionExitUpdate", StringComparison.OrdinalIgnoreCase));
        var local = exit != null ? RuleValue.Coord(exit.Get("UnitCreatePoint")) : new Vector3(building.GeometryMajorRadius + 20, 0, 0);
        var world = position + Vector3.Transform(local with { Z = 0 }, Matrix4x4.CreateRotationZ(angle));
        return game.Terrain.IsUnderwater(world.X, world.Y, out _);
    }

    static bool Overlaps(ObjectTemplate building, Vector3 position, float angle, GameObject other)
    {
        // Circle test against the larger extent; precise enough for placement.
        float a = Math.Max(building.GeometryMajorRadius, building.GeometryMinorRadius);
        return MathUtil.Distance2D(position, other.Position) < a + other.Radius * 0.8f;
    }

    /// <summary>Whether two rotated rectangles (centre, angle, half extents along X and Y) overlap (separating axes).</summary>
    static bool RectsOverlap(Vector3 aPos, float aAngle, float aHalfX, float aHalfY, Vector3 bPos, float bAngle, float bHalfX, float bHalfY)
    {
        var d = new Vector2(bPos.X - aPos.X, bPos.Y - aPos.Y);
        Span<Vector2> axes =
        [
            new(MathF.Cos(aAngle), MathF.Sin(aAngle)), new(-MathF.Sin(aAngle), MathF.Cos(aAngle)),
            new(MathF.Cos(bAngle), MathF.Sin(bAngle)), new(-MathF.Sin(bAngle), MathF.Cos(bAngle)),
        ];
        foreach (var axis in axes)
        {
            float ra = aHalfX * MathF.Abs(Vector2.Dot(axes[0], axis)) + aHalfY * MathF.Abs(Vector2.Dot(axes[1], axis));
            float rb = bHalfX * MathF.Abs(Vector2.Dot(axes[2], axis)) + bHalfY * MathF.Abs(Vector2.Dot(axes[3], axis));
            if (MathF.Abs(Vector2.Dot(d, axis)) >= ra + rb)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Places a building to be constructed: pays for it, creates it "under construction" and tells
    /// the builder to start. Returns null if the location is illegal or the player can't afford it.
    /// </summary>
    public GameObject? StartConstruction(ObjectTemplate building, Vector3 position, float angle, Player player, GameObject? builder)
    {
        if (CheckLocation(building, position, angle, player, builder) != BuildLocation.Ok)
            return null;
        int cost = game.Costs.BuildCost(player, building);
        if (!player.Money.TryWithdraw(cost))
            return null;

        position.Z = SurfaceHeight(position);
        var structure = game.CreateObject(building, player, position, angle, underConstruction: true);
        structure.BuilderId = builder?.Id ?? 0;
        structure.ConstructionCost = cost;

        // Units standing where the building goes get out of the way.
        foreach (var unit in game.ObjectsInRadius(position, structure.Radius))
            if (unit.AI != null && unit.IsMobile && unit != builder && unit.Owner == player)
                unit.AI.MoveTo(position + MathUtil.Forward(game.Random.Float(0, MathF.Tau)) * (structure.Radius + unit.Radius + 10), Commands.CommandSource.AI);

        builder?.FindModule<Modules.AI.DozerAIUpdate>()?.Construct(structure);
        return structure;
    }

    /// <summary>
    /// The original's isPossibleToMakeUnit: the builder has a build button for it and the player may
    /// build it (prerequisites, script permissions, maximum count).
    /// </summary>
    public bool IsPossibleToMakeUnit(GameObject? builder, ObjectTemplate? what)
    {
        if (builder == null || what == null)
            return false;
        var button = builder.CommandSetButtons().LastOrDefault(b => b.CommandType is "UNIT_BUILD" or "DOZER_CONSTRUCT"
            && game.Templates.Find(b.Object) is { } t && t.IsEquivalentTo(what));
        if (button == null || game.Templates.Find(button.Object) is not { } buildable)
            return false;
        return builder.Owner.CanBuild(buildable);
    }

    /// <summary>The original's canMakeUnit, as yes or no: possible, not disabled, room in the queue, and affordable.</summary>
    public bool CanMakeUnit(GameObject? builder, ObjectTemplate? what)
    {
        if (builder == null || what == null)
            return false;
        if (builder.HasStatus("SCRIPT_DISABLED") || builder.HasStatus("SCRIPT_UNPOWERED"))
            return false;
        if (!builder.Owner.CanBuildMoreOfType(what))
            return false;
        if (!IsPossibleToMakeUnit(builder, what))
            return false;
        if (builder.FindModule<Modules.Production.ProductionUpdate>() is { } production && production.CanQueueUnit(what) is { } reason && reason != "NotEnoughMoney")
            return false;
        return game.Costs.BuildCost(builder.Owner, what) <= builder.Owner.Money.Amount;
    }

    /// <summary>
    /// The original's buildObjectNow: a dozer is sent to construct it (paying as usual); anything else
    /// makes it appear finished on the spot (the computer player's "initially built" structures).
    /// </summary>
    public GameObject? BuildObjectNow(GameObject? builder, ObjectTemplate what, Vector3 position, float angle, Player owner)
    {
        if (builder != null && builder.FindModule<Modules.AI.DozerAIUpdate>() != null)
        {
            builder.AI?.Stop(Commands.CommandSource.AI);
            return StartConstruction(what, position, angle, owner, builder);
        }
        position.Z = SurfaceHeight(position);
        var obj = game.CreateObject(what, owner, position, angle);
        obj.ProducerId = builder?.Id ?? 0;
        if (obj.IsKindOf(Kind.Structure))
        {
            owner.Score.AddObjectBuilt(obj);
            owner.Score.AddMoneySpent(game.Costs.BuildCost(owner, what));
        }
        else
        {
            owner.OnUnitCreated(builder, obj);
            game.Events.PlaySound(what.UnitSound("VoiceCreated"), obj.Position, null, obj);
        }
        game.TreatAsBuilt(obj);
        return obj;
    }

    /// <summary>
    /// Sells a building: it is taken apart over a few seconds (shown with the SOLD model condition),
    /// then removed, and the owner gets GameData SellPercentage of its cost back.
    /// An unfinished building is cancelled instead, refunding what was paid.
    /// </summary>
    public void Sell(GameObject structure)
    {
        if (structure.HasStatus(Status.Sold) || structure.IsEffectivelyDead)
            return;

        if (structure.IsUnderConstruction)
        {
            structure.Owner.Money.Deposit(structure.ConstructionCost);
            game.DestroyObject(structure);
            return;
        }

        int refund = (int)(game.Costs.BuildCost(structure.Owner, structure.Template) * game.Catalog.GameData.SellPercentage);
        structure.SetStatus(Status.Sold);
        structure.SetStatus(Status.Unselectable);
        structure.Contain?.OnSelling(); // passengers walk out while it is taken apart
        structure.SetCondition(Condition.Sold);
        structure.Owner.Money.Deposit(refund);
        foreach (var production in structure.FindModules<Modules.Production.ProductionUpdate>())
            production.CancelAll();
        pendingSales.Add((structure, game.Frame + LogicTime.FramesFromSeconds(3)));
    }

    readonly List<(GameObject Structure, int RemoveFrame)> pendingSales = new();

    /// <summary>Finishes sales whose take-apart time is over (called once per frame by the logic).</summary>
    internal void Update()
    {
        for (int i = pendingSales.Count - 1; i >= 0; i--)
        {
            var (structure, removeFrame) = pendingSales[i];
            if (structure.IsDestroyed)
            {
                pendingSales.RemoveAt(i);
                continue;
            }
            structure.ConstructionPercent = 100f * Math.Max(0, removeFrame - game.Frame) / LogicTime.FramesFromSeconds(3);
            if (game.Frame >= removeFrame)
            {
                // Garrisoned units and passengers leave before the building disappears.
                structure.Contain?.RemoveAllContained();
                game.DestroyObject(structure);
                pendingSales.RemoveAt(i);
            }
        }
    }

    /// <summary>Advances construction by a fraction (0..1). Called by builders every frame they work.</summary>
    public void AddConstructionProgress(GameObject structure, float fraction)
    {
        if (!structure.IsUnderConstruction)
            return;
        float before = structure.ConstructionPercent;
        structure.ConstructionPercent = Math.Min(100f, structure.ConstructionPercent + fraction * 100f);
        structure.SetCondition(Condition.PartiallyConstructed, structure.ConstructionPercent < 100);
        structure.SetCondition(Condition.AwaitingConstruction, false);

        // Health grows with construction.
        if (structure.Body != null)
            structure.Body.AttemptHealing(structure.MaxHealth * (structure.ConstructionPercent - before) / 100f);

        if (structure.ConstructionPercent >= 100f)
            game.CompleteConstruction(structure);
    }
}
