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
        foreach (var other in game.ObjectsInRadius(position, radius))
        {
            if (other == builder || other.IsEffectivelyDead)
                continue;
            bool blocking = other.IsKindOf(Kind.Structure) || other.IsKindOf(Kind.Immobile) && !other.IsKindOf(Kind.Shrubbery) && !other.IsKindOf("CLEARED_BY_BUILD");
            if (blocking && Overlaps(building, position, angle, other))
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
    /// Dry, open ground just outside a shipyard's edge, where a dozer can stand to build it (the
    /// dozer works from within about its own size of the building's edge).
    /// </summary>
    bool HasDozerFooting(ObjectTemplate building, Vector3 position)
    {
        var terrain = game.Terrain;
        float radius = Math.Max(building.GeometryMajorRadius, building.GeometryMinorRadius);
        for (int i = 0; i < 24; i++)
        {
            float a = i * MathF.Tau / 24;
            var direction = new Vector3(MathF.Cos(a), MathF.Sin(a), 0);
            foreach (float extra in new[] { 6f, 12f })
            {
                var p = position + direction * (radius + extra);
                var (cx, cy) = terrain.CellAt(p.X, p.Y);
                if (!terrain.IsWaterCell(cx, cy) && !terrain.IsCliffCell(cx, cy))
                    return true;
            }
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

        position.Z = game.Terrain.GroundHeight(position);
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
        position.Z = game.Terrain.GroundHeight(position);
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
