using System.Numerics;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Modules.Production;
using OpenGZH.Logic.Modules.Supply;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Scripting;
using OpenGZH.Logic.Templates;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.AI;

/// <summary>
/// The computer player's brain (the original's AIPlayer): rebuilds its base from the build list
/// with dozers, builds the teams its scripts and production conditions ask for, keeps supply trucks
/// working, buys generals' powers from its skill set and repairs bridges. Mission scripts tell it
/// what to do; it does the legwork. <see cref="AISkirmishPlayer"/> adds the skirmish behaviour.
/// </summary>
public class AIPlayer
{
    const int MaxStructuresToRepair = 2;
    const int InvalidSkillsetSelection = -1;
    static readonly float SupplyCenterCloseDist = 20 * Formats.Maps.MapFile.CellSize;
    static readonly float CellSize = Formats.Maps.MapFile.CellSize;

    protected readonly Player player;
    protected readonly GameLogic game;
    protected readonly List<TeamInQueue> teamBuildQueue = new();
    protected readonly List<TeamInQueue> teamReadyQueue = new();

    protected bool readyToBuildTeam, readyToBuildStructure;
    protected int teamTimer = 2, structureTimer = 2, teamSeconds;
    protected int buildDelay, teamDelay;
    protected int frameLastBuildingBuilt;
    protected Difficulty difficulty;
    protected int skillsetSelector = InvalidSkillsetSelection;
    protected Vector3 baseCenter;
    protected bool baseCenterSet;
    protected float baseRadius;
    readonly uint[] structuresToRepair = new uint[MaxStructuresToRepair];
    uint repairDozer;
    Vector3 repairDozerOrigin;
    int structuresInQueue;
    bool dozerQueuedForRepair, dozerIsRepairing;
    int bridgeTimer;
    int supplySourceAttackCheckFrame;
    uint attackedSupplyCenter;
    uint curWarehouseId;

    public AIPlayer(Player p)
    {
        player = p;
        game = p.Game;
        frameLastBuildingBuilt = game.Frame;
        p.CanBuildUnits = false;
        difficulty = game.Scripts?.Difficulty ?? Difficulty.Normal;
        teamSeconds = (int)game.AIData.TeamSeconds;
    }

    protected AIData Data => game.AIData;
    protected ScriptEngine? Scripts => game.Scripts;
    void Debug(string message) => Scripts?.AppendDebugMessage(message, false);

    public virtual bool IsSkirmishAI => false;
    public virtual Player? GetAiEnemy() => null;
    public virtual bool CheckBridges(GameObject unit, Waypoint way) => false;

    public bool GetBaseCenter(out Vector3 pos)
    {
        pos = baseCenter;
        return baseCenterSet;
    }

    public Difficulty AIDifficulty
    {
        get => difficulty;
        set => difficulty = value;
    }

    public void SetTeamDelaySeconds(int delay) => teamSeconds = delay;

    IEnumerable<BuildListInfo> BuildList => player.BuildList;

    IEnumerable<GameObject> PlayerMembers() => player.Teams.ToList().SelectMany(t => t.AllMembers.ToList());

    // ---- Production feedback ----

    /// <summary>A dozer finished a structure on the build list: give it its map properties and script.</summary>
    public virtual void OnStructureProduced(GameObject? factory, GameObject bldg)
    {
        teamDelay = 0;
        buildDelay = 0;
        foreach (var info in BuildList)
        {
            if (info.ObjectId != bldg.Id)
                continue;
            info.IsUnderConstruction = false;
            ApplyBuildListProperties(bldg, info);
            bldg.SetStatus(Status.UnderConstruction, false);
            bldg.SetStatus("RECONSTRUCTING", false);
            bldg.CheckUpgradeModules();
            Scripts?.AddObjectToCache(bldg);
            Scripts?.RunObjectScript(info.Script, bldg);
            CheckForSupplyCenter(info, bldg);
            return;
        }
        // A structure rebuilt from a GLA rebuild hole takes the hole's place on the list.
        foreach (var info in BuildList)
        {
            if (info.ObjectId == 0 || game.Templates.Find(info.TemplateName) is not { } plan || !plan.IsEquivalentTo(bldg.Template))
                continue;
            if (game.FindObject(info.ObjectId) is { } obj && obj.IsKindOf("REBUILD_HOLE")
                && obj.FindModule<Modules.Behaviors.RebuildHoleBehavior>()?.ReconstructedBuildingId == bldg.Id)
            {
                info.ObjectId = bldg.Id;
                return;
            }
        }
    }

    void ApplyBuildListProperties(GameObject bldg, BuildListInfo info)
    {
        if (info.BuildingName.Length > 0)
            game.SetObjectName(bldg, info.BuildingName);
        if (info.Health != 100 && bldg.Body != null)
            bldg.Body.SetHealth(bldg.MaxHealth * info.Health / 100f);
        bldg.IsUnsellable = info.Unsellable;
    }

    void CheckForSupplyCenter(BuildListInfo info, GameObject bldg)
    {
        if (bldg.FindModule<SupplyCenterDockUpdate>() == null)
            return;
        int desired = 0;
        if (Data.SideInfo(player.Side) is { } side)
            desired = difficulty switch
            {
                Difficulty.Easy => side.ResourceGatherersEasy,
                Difficulty.Hard => side.ResourceGatherersHard,
                _ => side.ResourceGatherersNormal,
            };
        info.IsSupplyBuilding = true;
        info.CurrentGatherers = -1;
        info.DesiredGatherers = desired + 1;
    }

    /// <summary>
    /// The object's supply-gathering brain (the original's getSupplyTruckAIInterface): supply trucks and
    /// Chinooks, and GLA workers too, which gather as well as build.
    /// </summary>
    static SupplyGatherer? Gatherer(GameObject obj) => SupplyGatherer.Of(obj);

    void QueueSupplyTruck()
    {
        if (teamBuildQueue.Any(t => t.WorkOrders.Any(o => o.IsResourceGatherer)))
            return;
        int totalHarvesters = PlayerMembers().Count(o => o.IsKindOf(Kind.Harvester) && Gatherer(o) != null);
        foreach (var info in BuildList.ToList())
        {
            if (!info.IsSupplyBuilding)
                continue;
            int desiredGatherers = info.DesiredGatherers;
            if (info.CurrentGatherers >= desiredGatherers)
            {
                var supplyCenter = game.FindObject(info.ObjectId);
                if (supplyCenter == null || supplyCenter.IsKindOf("REBUILD_HOLE"))
                    continue;
                float radius = SupplyCenterCloseDist + supplyCenter.BoundingCircleRadius;
                var supplySource = game.ObjectsInRadius(supplyCenter.Position, radius)
                    .Where(o => o.IsKindOf(Kind.SupplySource) && o.Owner != player && !o.IsOffMap)
                    .MinBy(o => MathUtil.DistanceSquared2D(o.Position, supplyCenter.Position));
                if (supplySource == null)
                    continue;
                if (supplySource.FindModule<SupplyWarehouseDockUpdate>() is { } warehouse)
                {
                    int availableCash = warehouse.Boxes * player.SupplyBoxValue;
                    if (availableCash <= 0)
                        continue;
                    if (player.RelationshipTo(supplySource.Team) == Relationship.Enemies)
                        continue;
                }
                CheckForSupplyCenter(info, supplyCenter);
                int current = 0;
                foreach (var obj in PlayerMembers())
                {
                    if (!obj.IsKindOf(Kind.Harvester) || Gatherer(obj) is not { } gatherer)
                        continue;
                    if (gatherer.PreferredCenterId == supplyCenter.Id)
                    {
                        current++;
                        if (!gatherer.IsCurrentlyFerryingSupplies)
                            obj.AI!.Dock(supplyCenter, CommandSource.Player);
                    }
                }
                info.CurrentGatherers = current;
            }
            else
            {
                foreach (var obj in PlayerMembers())
                {
                    if (!obj.IsKindOf(Kind.Harvester) || Gatherer(obj) is not { } gatherer)
                        continue;
                    if (game.FindObject(gatherer.PreferredCenterId) != null)
                        continue;
                    if (gatherer.IsCurrentlyFerryingSupplies || gatherer.ForceWanting)
                    {
                        if (game.FindObject(info.ObjectId) is { } center)
                        {
                            info.CurrentGatherers++;
                            obj.AI!.Dock(center, CommandSource.Player);
                            return;
                        }
                    }
                }
                if (totalHarvesters >= desiredGatherers * 3)
                    continue;
                bool canBuildUnits = player.CanBuildUnits;
                player.CanBuildUnits = true;
                foreach (var template in AllTemplates())
                {
                    if (!template.Is("HARVESTER"))
                        continue;
                    var factory = FindFactory(template, false);
                    if (factory == null)
                        continue;
                    var order = new WorkOrder { Thing = template, NumRequired = 1, Required = true, IsResourceGatherer = true };
                    var team = new TeamInQueue { PriorityBuild = true, FrameStarted = game.Frame, Team = player.DefaultTeam };
                    team.WorkOrders.Add(order);
                    teamBuildQueue.Insert(0, team);
                    Debug("Supply truck - building one at the " + factory.Template.Name);
                    teamDelay = 0;
                    if (info.CurrentGatherers == -1)
                    {
                        order.FactoryId = factory.Id;
                        info.CurrentGatherers = 0;
                    }
                    else
                        StartTraining(order, team.PriorityBuild, team.Team.Name);
                    break;
                }
                player.CanBuildUnits = canBuildUnits;
            }
        }
    }

    /// <summary>Every object template, in the order the rules define them (the original's template list).</summary>
    protected IEnumerable<ObjectTemplate> AllTemplates()
    {
        foreach (string name in game.Templates.ObjectNames)
            if (game.Templates.Find(name) is { } template)
                yield return template;
    }

    void ClearTeamsInQueue()
    {
        teamBuildQueue.Clear();
        teamReadyQueue.Clear();
    }

    /// <summary>A build list structure appears finished on the spot ("initially built").</summary>
    protected GameObject? BuildStructureNow(ObjectTemplate plan, BuildListInfo info)
    {
        var bldg = game.Building.BuildObjectNow(null, plan, info.Location, info.Angle, player);
        if (bldg == null)
            return null;
        ApplyBuildListProperties(bldg, info);
        info.ObjectId = bldg.Id;
        info.ObjectTimestamp = game.Frame + 1;
        bldg.SetStatus(Status.UnderConstruction, false);
        bldg.SetStatus("RECONSTRUCTING", false);
        bldg.CheckUpgradeModules();
        Scripts?.AddObjectToCache(bldg);
        Scripts?.RunObjectScript(info.Script, bldg);
        CheckForSupplyCenter(info, bldg);
        // (The original's rally point code never applies its offset.)
        return bldg;
    }

    bool LocationOk(ObjectTemplate plan, Vector3 pos, float angle, GameObject? builder) =>
        game.Building.CheckLocation(plan, pos, angle, player, builder) == Construction.BuildLocation.Ok;

    /// <summary>Sends a dozer to build a structure, nudging the spot around if something is in the way.</summary>
    protected GameObject? BuildStructureWithDozer(ObjectTemplate plan, BuildListInfo info)
    {
        var dozer = FindDozer(info.Location);
        if (dozer == null)
            return null;
        if (player.Money.Amount < game.Costs.BuildCost(player, plan))
            return null;
        var pos = info.Location;
        pos.Z = game.Terrain.GroundHeight(pos);
        if (dozer.AI == null)
            return null;
        float angle = info.Angle;
        if (!LocationOk(plan, pos, angle, dozer))
        {
            Debug(plan.Name + " - Dozer unable to place.  Attempting to adjust position.");
            bool valid = false;
            float limit = 10 * CellSize;
            if (IsSkirmishAI)
                limit = 120 * CellSize;
            var newPos = pos;
            for (float posOffset = 0; posOffset < limit; posOffset += 2 * CellSize)
            {
                if (IsSkirmishAI)
                    posOffset += 2 * CellSize;
                float offset = posOffset / 2;
                float yPos = pos.Y - offset, xPos;
                for (xPos = pos.X - offset; xPos <= pos.X + offset; xPos += CellSize)
                {
                    if (IsSkirmishAI)
                        xPos += CellSize;
                    newPos.X = xPos;
                    newPos.Y = yPos;
                    if (valid = LocationOk(plan, newPos, angle, dozer))
                        break;
                    newPos.Y = yPos + posOffset;
                    if (valid = LocationOk(plan, newPos, angle, dozer))
                        break;
                }
                if (valid)
                    break;
                xPos = pos.X - offset;
                for (yPos = pos.Y - offset; yPos <= pos.Y + offset; yPos += CellSize)
                {
                    if (IsSkirmishAI)
                        yPos += CellSize;
                    newPos.X = xPos;
                    newPos.Y = yPos;
                    if (valid = LocationOk(plan, newPos, angle, dozer))
                        break;
                    newPos.X = xPos + posOffset;
                    if (valid = LocationOk(plan, newPos, angle, dozer))
                        break;
                }
                if (valid)
                    break;
            }
            if (!valid)
                return null;
            pos = newPos;
        }
        if (game.Pathfinder.FindPath(dozer.Position, pos, dozer.AI.Surfaces).Count == 0
            && MathUtil.Distance2D(dozer.Position, pos) > 2 * CellSize)
        {
            Debug(plan.Name + " - Dozer unable to reach building.  Teleporting.");
            dozer.AI.Teleport(pos);
        }
        var bldg = game.Building.BuildObjectNow(dozer, plan, pos, angle, player);
        if (bldg != null)
        {
            info.ObjectId = bldg.Id;
            info.ObjectTimestamp = game.Frame + 1;
            info.IsUnderConstruction = true;
        }
        return bldg;
    }

    /// <summary>Picks up where a dozer left a building (its builder died or wandered off).</summary>
    protected void ResumeConstruction(GameObject dozer, GameObject bldg) => (dozer.AI as DozerAIUpdate)?.Construct(bldg);

    protected virtual void ProcessBaseBuilding()
    {
        if (!readyToBuildStructure)
            return;
        foreach (var info in BuildList.ToList())
        {
            string name = info.TemplateName;
            if (name.Length == 0)
                continue;
            var plan = game.Templates.Find(name);
            if (plan == null)
                continue;
            if (info.ObjectId != 0)
            {
                var existing = game.FindObject(info.ObjectId);
                if (existing == null)
                {
                    uint priorId = info.ObjectId;
                    info.ObjectId = 0;
                    info.ObjectTimestamp = game.Frame + 1;
                    // A GLA building may have left a rebuild hole: follow it.
                    foreach (var obj in game.Objects)
                        if (obj.IsKindOf("REBUILD_HOLE") && obj.FindModule<Modules.Behaviors.RebuildHoleBehavior>() is { } hole && hole.SpawnerId == priorId)
                            info.ObjectId = obj.Id;
                }
                else if (existing.Owner == player)
                {
                    if (existing.IsUnderConstruction)
                    {
                        var myDozer = game.FindObject(existing.BuilderId) ?? FindDozer(existing.Position);
                        if (myDozer?.AI == null)
                            continue;
                        ResumeConstruction(myDozer, existing);
                    }
                }
                else
                {
                    info.ObjectId = 0;
                    info.ObjectTimestamp = game.Frame + 1;
                }
            }
            if (info.ObjectId == 0 && info.ObjectTimestamp > 0)
            {
                if (info.ObjectTimestamp + Data.RebuildDelaySeconds * LogicTime.FramesPerSecond > game.Frame)
                    continue;
                info.ObjectTimestamp = 0;
            }
            if (!info.IsBuildable || game.FindObject(info.ObjectId) != null)
                continue;
            var bldg = BuildStructureWithDozer(plan, info);
            if (bldg != null)
            {
                info.ObjectId = bldg.Id;
                info.DecrementNumRebuilds();
                StructureBuilt();
                break;
            }
        }
    }

    /// <summary>The structure timer restarts, shorter when poor or rich (the original's StructuresPoorRate / WealthyRate).</summary>
    protected void StructureBuilt()
    {
        readyToBuildStructure = false;
        float timer = Data.StructureSeconds * LogicTime.FramesPerSecond;
        if (player.Money.Amount < Data.Poor)
            timer /= Data.StructuresPoorRate;
        else if (player.Money.Amount > Data.Wealthy)
            timer /= Data.StructuresWealthyRate;
        structureTimer = (int)timer;
        frameLastBuildingBuilt = game.Frame;
    }

    /// <summary>A team is being deleted: forget its queue entries.</summary>
    public void AIPreTeamDestroy(Team team)
    {
        teamBuildQueue.RemoveAll(t => t.Team == team);
        teamReadyQueue.RemoveAll(t => t.Team == team);
    }

    /// <summary>TEAM_GUARD_SUPPLY_CENTER: guard the supply source that is under attack, else the best one.</summary>
    public void GuardSupplyCenter(Team team, int minSupplies)
    {
        supplySourceAttackCheckFrame = 0;
        GameObject? warehouse = null;
        if (IsSupplySourceAttacked())
            warehouse = game.FindObject(attackedSupplyCenter);
        warehouse ??= FindSupplyCenter(minSupplies);
        if (warehouse == null)
            return;
        var group = AIGroup.FromTeam(team);
        var location = warehouse.Position;
        var enemy = Scripts?.GetSkirmishEnemyPlayer();
        var bounds = GetPlayerStructureBounds(enemy?.Index ?? 0);
        var offset = new Vector2(location.X - (bounds.Lo.X + bounds.Hi.X) * 0.5f, location.Y - (bounds.Lo.Y + bounds.Hi.Y) * 0.5f);
        offset = Normalize(offset);
        float radius = warehouse.BoundingCircleRadius * 0.8f;
        location.X -= offset.X * radius;
        location.Y -= offset.Y * radius;
        group.GroupGuardPosition(location, CommandSource.Script);
    }

    static Vector2 Normalize(Vector2 v) => v.LengthSquared() > 0 ? Vector2.Normalize(v) : v;

    public bool IsSupplySourceAttacked()
    {
        const int ScanRate = 10;
        int curFrame = game.Frame;
        if (curFrame == 0)
        {
            supplySourceAttackCheckFrame = curFrame + ScanRate;
            return false;
        }
        attackedSupplyCenter = 0;
        if (curFrame < supplySourceAttackCheckFrame)
            return false;
        if (player.AttackedFrame + ScanRate < curFrame)
            return false;
        supplySourceAttackCheckFrame = curFrame + ScanRate;
        foreach (var obj in PlayerMembers())
        {
            if (!obj.IsKindOf("CASH_GENERATOR") && !obj.IsKindOf(Kind.Dozer) && !obj.IsKindOf(Kind.Harvester))
                continue;
            if (obj.Body is { LastDamage: not null } body && !body.LastDamageNoEffect && body.LastDamageFrame + ScanRate > curFrame)
            {
                attackedSupplyCenter = obj.Id;
                return true;
            }
        }
        return false;
    }

    public bool IsSupplySourceSafe(int minSupplies)
    {
        var warehouse = FindSupplyCenter(minSupplies);
        return warehouse == null || IsLocationSafe(warehouse.Position, warehouse.Template);
    }

    /// <summary>No enemy (other than harvesters, dozers and hidden or insignificant things) near the spot.</summary>
    protected bool IsLocationSafe(Vector3 pos, ObjectTemplate? template)
    {
        if (template == null)
            return false;
        float radius = Data.SupplyCenterSafeRadius + template.Geometry.BoundingCircleRadius;
        foreach (var o in game.ObjectsInRadius(pos, radius + 50))
        {
            if (o.IsEffectivelyDead || player.RelationshipTo(o.Team) != Relationship.Enemies)
                continue;
            if (o.HasStatus(Status.Stealthed) && !o.HasStatus(Status.Detected) && !o.HasStatus("DISGUISED"))
                continue;
            if (o.IsKindOf(Kind.Harvester) || o.IsKindOf(Kind.Dozer))
                continue;
            if (o.IsKindOf(Kind.Structure) && !o.IsFactionStructure && o.Contain is not { Count: > 0 })
                continue; // insignificant
            if (MathUtil.Distance2D(o.Position, pos) - o.BoundingSphereRadius <= radius)
                return false;
        }
        return true;
    }

    public virtual void OnUnitProduced(GameObject? factory, GameObject unit)
    {
        bool found = false, supplyTruck = false;
        if (factory == null)
            return;
        foreach (var team in teamBuildQueue.ToList())
        {
            if (found)
                break;
            foreach (var order in team.WorkOrders)
            {
                if (order.FactoryId != factory.Id || order.NumCompleted >= order.NumRequired || !unit.Template.IsEquivalentTo(order.Thing))
                    continue;
                order.NumCompleted++;
                if (team.Team != null)
                    unit.SetTeam(team.Team);
                if (team.Reinforcement)
                    team.ReinforcementId = unit.Id;
                var ai = unit.AI;
                if (team.Team?.Prototype.TemplateInfo is { HasHomeLocation: true } info && ai != null)
                    ai.FollowExitProductionPath([ai.Destination, info.HomeLocation], factory);
                order.FactoryId = 0;
                if (ai != null && Gatherer(unit) is { } gatherer)
                {
                    supplyTruck = order.IsResourceGatherer;
                    gatherer.ForceWanting = supplyTruck;
                    if (supplyTruck)
                        foreach (var bl in BuildList)
                            if (bl.IsSupplyBuilding && bl.DesiredGatherers > 0 && bl.DesiredGatherers > bl.CurrentGatherers
                                && game.FindObject(bl.ObjectId) is { } center)
                            {
                                bl.CurrentGatherers++;
                                ai.Dock(center, CommandSource.Player);
                            }
                }
                found = true;
                break;
            }
        }
        if (!supplyTruck && unit.IsKindOf(Kind.Dozer))
        {
            if (dozerQueuedForRepair)
            {
                repairDozer = unit.Id;
                dozerQueuedForRepair = false;
            }
            else
            {
                buildDelay = 0;
                structureTimer = 1;
            }
        }
        teamDelay = 0;
    }

    // ---- Superweapon targets ----

    /// <summary>
    /// Where a superweapon does the most damage to a player: a coarse 10x10 search over its base,
    /// then a fine one around the best spot (the original's diagonal-only fine search is kept).
    /// </summary>
    public virtual bool ComputeSuperweaponTarget(SpecialPowerTemplate power, out Vector3 retPos, int playerNdx, float weaponRadius)
    {
        var bounds = GetPlayerStructureBounds(playerNdx);
        if (bounds.Hi == Vector2.Zero && bounds.Lo == Vector2.Zero)
            bounds = (Vector2.Zero, new Vector2(game.Terrain.WorldWidth, game.Terrain.WorldHeight));
        if (weaponRadius < 1)
            weaponRadius = 1;
        var lo = bounds.Lo;
        var hi = bounds.Hi;
        lo.X += weaponRadius;
        hi.X -= weaponRadius;
        if (hi.X < lo.X)
            hi.X = lo.X = (hi.X + lo.X) / 2;
        if (hi.Y < lo.Y)
            hi.Y = lo.Y = (hi.Y + lo.Y) / 2;
        float width = hi.X - lo.X, height = hi.Y - lo.Y;
        int xCount = Math.Min(10, (int)MathF.Ceiling(width / weaponRadius) + 1);
        int yCount = Math.Min(10, (int)MathF.Ceiling(height / weaponRadius) + 1);
        int cash = -1;
        var bestPos = Vector3.Zero;
        bool targetMilitaryUnits = !power.IsType("SPECIAL_SNEAK_ATTACK");
        int xDelta, yDelta, xStart, yStart;
        switch (game.Random.Int(1, 4))
        {
            case 1: xDelta = 1; yDelta = 1; xStart = 0; yStart = 0; break;
            case 2: xDelta = -1; yDelta = 1; xStart = xCount; yStart = 0; break;
            case 3: xDelta = 1; yDelta = -1; xStart = 0; yStart = yCount; break;
            default: xDelta = -1; yDelta = -1; xStart = xCount; yStart = yCount; break;
        }
        int xIndex = xStart;
        for (int x = 0; x < xCount; x++, xIndex += xDelta)
        {
            int yIndex = yStart;
            for (int y = 0; y < yCount; y++, yIndex += yDelta)
            {
                var pos = new Vector3(lo.X + width * xIndex / xCount, lo.Y + height * yIndex / yCount, 0);
                int curCash = GetPlayerSuperweaponValue(pos, playerNdx, 2 * weaponRadius, targetMilitaryUnits);
                if (curCash > cash)
                {
                    cash = curCash;
                    bestPos = pos;
                }
            }
        }
        var veryBest = Vector3.Zero;
        cash = -1;
        int count = 0;
        for (int x = 0; x < 11; x++)
            for (int y = 0; y < 11; y++)
            {
                var pos = new Vector3(bestPos.X + (x - 5) * (weaponRadius / 10), bestPos.Y + (x - 5) * (weaponRadius / 10), 0);
                int curCash = GetPlayerSuperweaponValue(pos, playerNdx, weaponRadius, targetMilitaryUnits);
                if (curCash > cash)
                {
                    cash = curCash;
                    veryBest = pos;
                    count = 1;
                }
                else if (curCash == cash)
                {
                    veryBest.X += pos.X;
                    veryBest.Y += pos.Y;
                    count++;
                }
            }
        if (count > 1)
        {
            veryBest.X /= count;
            veryBest.Y /= count;
        }
        veryBest.Z = game.Terrain.GroundHeight(veryBest);
        retPos = veryBest;
        return cash > -1;
    }

    int GetPlayerSuperweaponValue(Vector3 center, int playerNdx, float radius, bool includeMilitaryUnits)
    {
        if (radius < 4 * CellSize)
            radius = 4 * CellSize;
        if (playerNdx < 0 || playerNdx >= game.Players.All.Count)
            return 0;
        var target = game.Players.All[playerNdx];
        float cash = 0, radSqr = radius * radius;
        foreach (var obj in target.Teams.ToList().SelectMany(t => t.AllMembers))
        {
            bool applyNegValue = false;
            if (!includeMilitaryUnits)
            {
                if (obj.IsKindOf("FS_BASE_DEFENSE") || obj.IsKindOf("TECH_BASE_DEFENSE"))
                    applyNegValue = true;
                else if ((obj.IsKindOf(Kind.Vehicle) || obj.IsKindOf(Kind.Infantry)) && !obj.IsKindOf(Kind.Dozer) && !obj.IsKindOf(Kind.Harvester))
                    applyNegValue = true;
            }
            else if (obj.IsKindOf(Kind.Aircraft) && obj.IsSignificantlyAboveTerrain)
                continue;
            float dx = center.X - obj.Position.X, dy = center.Y - obj.Position.Y;
            if (dx * dx + dy * dy >= radSqr)
                continue;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float factor = 1 - dist / (2 * radius);
            float value = game.Costs.BuildCost(target, obj.Template);
            if (obj.IsKindOf("COMMANDCENTER"))
                value = includeMilitaryUnits ? value / 10 : value * 5;
            if (obj.IsKindOf("FS_SUPERWEAPON"))
                value = includeMilitaryUnits ? value / 10 : value * 5;
            if (applyNegValue)
                cash -= factor * value * 5;
            else
                cash += factor * value;
        }
        return (int)cash;
    }

    // ---- Factories and teams ----

    protected virtual bool StartTraining(WorkOrder order, bool busyOK, string teamName)
    {
        var factory = FindFactory(order.Thing, busyOK);
        if (factory?.FindModule<ProductionUpdate>() is { } production && production.QueueUnit(order.Thing) != null)
        {
            order.FactoryId = factory.Id;
            return true;
        }
        return false;
    }

    /// <summary>An idle factory on the build list that can make this (or a busy one, if allowed).</summary>
    protected GameObject? FindFactory(ObjectTemplate thing, bool busyOK)
    {
        GameObject? busyFactory = null;
        foreach (var info in BuildList)
        {
            var factory = game.FindObject(info.ObjectId);
            if (factory == null)
                continue;
            if (factory.Owner != player)
            {
                info.ObjectId = 0;
                continue;
            }
            if (factory.IsUnderConstruction || factory.HasStatus(Status.Sold))
                continue;
            if (factory.FindModule<ProductionUpdate>() is not { } production)
                continue;
            if (!game.Building.IsPossibleToMakeUnit(factory, thing))
                continue;
            if (production.Queue.Count == 0)
                return factory;
            if (busyOK)
                busyFactory = factory;
        }
        return busyOK ? busyFactory : null;
    }

    protected bool IsPossibleToBuildTeam(TeamPrototype proto, bool requireIdleFactory, out bool notEnoughMoney)
    {
        bool anyIdle = false;
        float cost = 0;
        notEnoughMoney = false;
        foreach (var unitInfo in proto.TemplateInfo.Units)
        {
            if (game.Templates.Find(unitInfo.UnitThingName) is not { } thing)
                continue;
            int thingCost = game.Costs.BuildCost(player, thing);
            if (FindFactory(thing, true) == null)
                return false;
            if (FindFactory(thing, false) != null)
                anyIdle = true;
            cost += (int)(thingCost * ((unitInfo.MaxUnits + unitInfo.MinUnits) / 2.0f));
        }
        cost = (int)(cost * Data.TeamResourcesToStart);
        if (player.Money.Amount < cost)
        {
            notEnoughMoney = true;
            return false;
        }
        return anyIdle || !requireIdleFactory;
    }

    protected virtual bool IsAGoodIdeaToBuildTeam(TeamPrototype proto)
    {
        if (!proto.EvaluateProductionCondition())
            return false;
        if (proto.CountTeamInstances() >= proto.TemplateInfo.MaxInstances)
            return false;
        if (teamBuildQueue.Any(t => t.Team?.Prototype == proto))
            return false;
        return IsPossibleToBuildTeam(proto, true, out _);
    }

    protected virtual bool SelectTeamToReinforce(int minPriority)
    {
        Team? curTeam = null;
        int curPriority = minPriority;
        ObjectTemplate? curThing = null;
        foreach (var proto in player.TeamPrototypes.ToList())
        {
            if (teamBuildQueue.Any(t => t.Team?.Prototype == proto))
                continue;
            if (!proto.TemplateInfo.AutomaticallyReinforce || proto.TemplateInfo.ProductionPriority <= curPriority)
                continue;
            foreach (var team in proto.Instances)
            {
                if (!team.HasAnyUnits())
                    continue;
                foreach (var unitInfo in proto.TemplateInfo.Units)
                {
                    if (unitInfo.MaxUnits < 1 || game.Templates.Find(unitInfo.UnitThingName) is not { } thing)
                        continue;
                    int count = team.CountObjectsByTemplate(thing, ignoreDead: false);
                    if (count < unitInfo.MaxUnits && FindFactory(thing, false) != null)
                    {
                        curTeam = team;
                        curPriority = proto.TemplateInfo.ProductionPriority;
                        curThing = thing;
                    }
                }
            }
        }
        if (curTeam == null || curThing == null)
            return false;
        var teamQ = new TeamInQueue { PriorityBuild = false, Reinforcement = true, FrameStarted = game.Frame, Team = curTeam };
        var order = new WorkOrder { Thing = curThing, NumRequired = 1, Required = true };
        teamQ.WorkOrders.Add(order);
        teamBuildQueue.Insert(0, teamQ);
        Debug($"{curTeam.Prototype.Name} - AutoReinforcing one {curThing.Name}");
        var origin = curTeam.Prototype.TemplateInfo.HomeLocation;
        if (curTeam.AllMembers.Count > 0)
            origin = curTeam.AllMembers[0].Position;
        var unit = curTeam.TryToRecruit(curThing, origin, Data.MaxRecruitRadius);
        if (unit != null)
        {
            order.NumCompleted = 1;
            Debug($"Team '{curTeam.Prototype.Name}' recruits {curThing.Name} from team '{unit.Team?.Prototype.Name}'");
            unit.SetTeam(curTeam);
            teamQ.ReinforcementId = unit.Id;
            unit.AI?.Stop(CommandSource.AI);
        }
        else
            StartTraining(order, teamQ.PriorityBuild, curTeam.Name);
        teamDelay = 0;
        return true;
    }

    protected virtual bool SelectTeamToBuild()
    {
        const int InvalidPri = -99999;
        int hiPri = InvalidPri;
        var candidates1 = new List<TeamPrototype>();
        foreach (var proto in player.TeamPrototypes.ToList())
            if (IsAGoodIdeaToBuildTeam(proto))
            {
                candidates1.Add(proto);
                hiPri = Math.Max(hiPri, proto.TemplateInfo.ProductionPriority);
            }
        if (SelectTeamToReinforce(hiPri))
            return true;
        if (hiPri == InvalidPri)
            return false;
        var candidates = candidates1.Where(p => p.TemplateInfo.ProductionPriority == hiPri).ToList();
        int which = game.Random.Int(0, candidates.Count - 1);
        var teamProto = which >= 0 && which < candidates.Count ? candidates[which] : null;
        if (teamProto == null)
            return false;
        if (!teamProto.TemplateInfo.HasHomeLocation && !IsSkirmishAI)
            Debug($"Error : team '{teamProto.Name}' has no Home Position (or Origin).");
        BuildSpecificAITeam(teamProto, false);
        readyToBuildTeam = false;
        float timer = teamSeconds * LogicTime.FramesPerSecond;
        if (player.Money.Amount < Data.Poor)
            timer /= Data.TeamsPoorRate;
        else if (player.Money.Amount > Data.Wealthy)
            timer /= Data.TeamsWealthyRate;
        teamTimer = (int)timer;
        return true;
    }

    public virtual void BuildSpecificAIBuilding(string thingName) =>
        Debug($"Error : Solo ai doesn't support BuildSpecificBuilding. '{thingName}' not built.");

    /// <summary>AI_PLAYER_BUILD_UPGRADE: queue a player upgrade at a build list building that offers it.</summary>
    public void BuildUpgrade(string upgrade)
    {
        var node = game.Catalog.Upgrade(upgrade);
        string name = player.Name;
        if (node == null)
        {
            Debug($"Upgrade {upgrade} does not exist.  Ignoring request.");
            return;
        }
        if (node.GetString("Type", "PLAYER").Equals("OBJECT", StringComparison.OrdinalIgnoreCase))
        {
            Debug($"Player build upgrade: Upgrade {upgrade} is an object, not a player upgrade.  Ignoring request.");
            return;
        }
        if (game.ObjectsOwnedBy(player).Any(o => o.FindModule<ProductionUpdate>()?.Queue.Any(e => e.Upgrade == upgrade) == true))
        {
            Debug($"{name} already has upgrade {upgrade} queued.  Ignoring request.");
            return;
        }
        if (player.HasUpgrade(upgrade))
        {
            Debug($"{name} already has upgrade {upgrade} completed.  Ignoring request.");
            return;
        }
        if (player.Money.Amount < OpenGZH.Core.Rules.RuleValue.Int(node.GetString("BuildCost")))
        {
            Debug($"{name} lacks money to build upgrade {upgrade} at this time.  Ignoring request.");
            return;
        }
        foreach (var info in BuildList)
        {
            var factory = game.FindObject(info.ObjectId);
            if (factory == null || factory.IsUnderConstruction || factory.HasStatus(Status.Sold))
                continue;
            if (!factory.CommandSetButtons().Any(b => b.Upgrade.Equals(upgrade, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (factory.FindModule<ProductionUpdate>() is not { } production)
                continue;
            if (production.QueueUpgrade(upgrade) != null)
            {
                Debug($"{name} queues {upgrade} at {factory.Template.Name}");
                return;
            }
        }
        Debug($"{name} lacks factory to build upgrade {upgrade} at this time.  Ignoring request.");
    }

    bool SpiralSearch(ObjectTemplate template, Vector3 location, float angle, out Vector3 found)
    {
        found = location;
        if (LocationOk(template, location, angle, null))
            return true;
        var newPos = location;
        for (float posOffset = 0; posOffset < 2 * SupplyCenterCloseDist; posOffset += 2 * CellSize)
        {
            float offset = posOffset / 2;
            float yPos = location.Y - offset, xPos;
            for (xPos = location.X - offset; xPos <= location.X + offset; xPos += CellSize)
            {
                newPos.X = xPos;
                newPos.Y = yPos;
                if (LocationOk(template, newPos, angle, null)) { found = newPos; return true; }
                newPos.Y = yPos + posOffset;
                if (LocationOk(template, newPos, angle, null)) { found = newPos; return true; }
            }
            xPos = location.X - offset;
            for (yPos = location.Y - offset; yPos <= location.Y + offset; yPos += CellSize)
            {
                newPos.X = xPos;
                newPos.Y = yPos;
                if (LocationOk(template, newPos, angle, null)) { found = newPos; return true; }
                newPos.X = xPos + posOffset;
                if (LocationOk(template, newPos, angle, null)) { found = newPos; return true; }
            }
        }
        return false;
    }

    /// <summary>AI_PLAYER_BUILD_SUPPLY_CENTER: a building next to the best free supply source.</summary>
    public void BuildBySupplies(int minimumCash, string thingName)
    {
        var best = FindSupplyCenter(minimumCash);
        var template = game.Templates.Find(thingName);
        if (template == null)
            return;
        if (!template.Is("CASH_GENERATOR") && game.FindObject(curWarehouseId) is { } current)
            best = current;
        if (best == null)
            return;
        var location = best.Position;
        var offset = Normalize(new Vector2(location.X - baseCenter.X, location.Y - baseCenter.Y));
        float radius = 3 * CellSize;
        if (!template.Is("CASH_GENERATOR"))
        {
            var bounds = GetPlayerStructureBounds(Scripts?.GetSkirmishEnemyPlayer()?.Index ?? 0);
            offset = Normalize(new Vector2(location.X - (bounds.Lo.X + bounds.Hi.X) * 0.5f, location.Y - (bounds.Lo.Y + bounds.Hi.Y) * 0.5f));
            radius = best.BoundingCircleRadius;
        }
        location.X -= offset.X * radius;
        location.Y -= offset.Y * radius;
        float angle = template.PlacementViewAngle;
        if (SpiralSearch(template, location, angle, out var found))
            location = found;
        location.Z = 0;
        player.AddToPriorityBuildList(thingName, location, angle);
        curWarehouseId = best.Id;
    }

    /// <summary>Nearest legal spot to build at (a sneak attack's tunnel exit).</summary>
    public bool CalcClosestConstructionZoneLocation(ObjectTemplate template, ref Vector3 location)
    {
        float angle = template.PlacementViewAngle;
        if (SpiralSearch(template, location, angle, out var found))
        {
            location = found;
            return true;
        }
        location = Vector3.Zero;
        return false;
    }

    public void BuildSpecificBuildingNearestTeam(string thingName, Team team)
    {
        var template = game.Templates.Find(thingName);
        if (template == null || team.EstimateTeamPosition is not { } location)
            return;
        float angle = template.PlacementViewAngle;
        // (As the original: only a spot found by the search is used; a legal first guess is ignored.)
        if (!LocationOk(template, location, angle, null) && SpiralSearch(template, location, angle, out var found))
        {
            found.Z = 0;
            player.AddToPriorityBuildList(thingName, found, angle);
        }
    }

    /// <summary>The nearest supply source with enough cash that has no own supply center yet and isn't nearer the enemy.</summary>
    protected GameObject? FindSupplyCenter(int minimumCash)
    {
        GameObject? best = null;
        float bestDistSqr = 0;
        var enemy = GetAiEnemy();
        var enemyCenter = Vector3.Zero;
        if (enemy != null)
        {
            var bounds = GetPlayerStructureBounds(enemy.Index);
            enemyCenter = new Vector3((bounds.Lo.X + bounds.Hi.X) * 0.5f, (bounds.Lo.Y + bounds.Hi.Y) * 0.5f, 0);
        }
        do
        {
            foreach (var obj in game.Objects)
            {
                if (!obj.IsKindOf(Kind.Structure) || !obj.IsKindOf(Kind.SupplySource))
                    continue;
                if (obj.FindModule<SupplyWarehouseDockUpdate>() is not { } warehouse)
                    continue;
                if (warehouse.Boxes * player.SupplyBoxValue < minimumCash)
                    continue;
                if (player.RelationshipTo(obj.Team) == Relationship.Enemies)
                    continue;
                float radius = SupplyCenterCloseDist + obj.BoundingCircleRadius;
                if (game.ObjectsInRadius(obj.Position, radius).Any(o => o.IsKindOf("CASH_GENERATOR") && o.Owner == player && !o.IsOffMap))
                    continue;
                float dx = obj.Position.X - baseCenter.X, dy = obj.Position.Y - baseCenter.Y;
                float distSqr = dx * dx + dy * dy;
                if (enemy != null)
                {
                    dx = obj.Position.X - enemyCenter.X;
                    dy = obj.Position.Y - enemyCenter.Y;
                    if (distSqr * 0.4f > (dx * dx + dy * dy) * 0.6f)
                        continue;
                }
                if (best == null || bestDistSqr > distSqr)
                {
                    best = obj;
                    bestDistSqr = distSqr;
                }
            }
            if (best != null)
                break;
            minimumCash /= 2;
        } while (minimumCash > 100);
        return best;
    }

    public virtual void BuildAIBaseDefense(bool flank) => Debug("Error : Solo ai doesn't support buildAIBaseDefense. '");
    public virtual void BuildAIBaseDefenseStructure(string thingName, bool flank) => Debug("Error : Solo ai doesn't support buildAIBaseDefenseStructure. '");

    /// <summary>Queues a damaged structure (bridges, mostly) for a dozer to repair; two at most.</summary>
    public virtual void RepairStructure(uint structure)
    {
        var obj = game.FindObject(structure);
        if (obj?.Body == null || obj.Body.DamageState == Modules.BodyDamageState.Pristine)
            return;
        for (int i = 0; i < structuresInQueue; i++)
            if (structuresToRepair[i] == obj.Id)
                return;
        if (structuresInQueue == MaxStructuresToRepair)
            return;
        structuresToRepair[structuresInQueue++] = obj.Id;
    }

    public virtual void SelectSkillset(int skillset) => skillsetSelector = skillset;

    void UpdateBridgeRepair()
    {
        if (structuresInQueue == 0)
            return;
        if (--bridgeTimer > 0)
            return;
        bridgeTimer = LogicTime.FramesPerSecond;
        GameObject? bridge = null;
        while (bridge == null && structuresInQueue > 0)
        {
            bridge = game.FindObject(structuresToRepair[0]);
            if (bridge == null)
                RemoveFirstRepair();
        }
        if (structuresInQueue == 0 || bridge == null)
            return;
        var bridgePos = bridge.Position;
        var bridgeState = bridge.Body?.DamageState ?? Modules.BodyDamageState.Pristine;
        GameObject? dozer;
        if (repairDozer == 0)
        {
            dozerIsRepairing = false;
            if (dozerQueuedForRepair)
                return;
            dozer = FindDozer(bridgePos);
            if (dozer != null)
            {
                repairDozer = dozer.Id;
                repairDozerOrigin = dozer.Position;
                (dozer.AI as DozerAIUpdate)?.Repair(bridge);
                dozerIsRepairing = true;
                return;
            }
            QueueDozer();
            dozerQueuedForRepair = true;
            return;
        }
        dozer = game.FindObject(repairDozer);
        if (dozer == null)
        {
            repairDozer = 0;
            bridgeTimer = 0;
            return;
        }
        if (dozer.AI is not DozerAIUpdate dozerAI)
            return;
        if (dozerIsRepairing)
        {
            if (dozerAI.Task != DozerAIUpdate.DozerTask.None)
                return;
            if (bridgeState == Modules.BodyDamageState.Pristine)
            {
                RemoveFirstRepair();
                dozerIsRepairing = false;
                if (structuresInQueue == 0)
                {
                    var pos = baseCenterSet ? baseCenter : repairDozerOrigin;
                    pos = game.Pathfinder.NearestPassable(pos, dozerAI.Surfaces) ?? pos;
                    dozerAI.MoveTo(pos, CommandSource.AI);
                    return;
                }
            }
        }
        dozerAI.Repair(bridge);
        dozerIsRepairing = true;
    }

    void RemoveFirstRepair()
    {
        for (int i = 0; i < structuresInQueue - 1; i++)
            structuresToRepair[i] = structuresToRepair[i + 1];
        structuresInQueue--;
    }

    /// <summary>Queues a team's units: required (minimum) counts first, then the rest up to the maximum.</summary>
    public virtual void BuildSpecificAITeam(TeamPrototype? teamProto, bool priorityBuild)
    {
        if (teamProto == null)
            return;
        if (!player.CanBuildUnits)
        {
            Debug($"Can't build team '{teamProto.Name}' because build units is disabled.");
            return;
        }
        if (priorityBuild && teamProto.IsSingleton && game.Teams.FindTeam(teamProto.Name) is { } singleton && singleton.HasAnyObjects())
        {
            Debug("Unable to build singleton team '' because team already exists.");
            return;
        }
        if (!IsPossibleToBuildTeam(teamProto, false, out bool needMoney))
        {
            if (needMoney)
                Debug($"Note - queueing team '{teamProto.Name}' but there is enough money.");
            else
            {
                Debug($"Unable to build team '{teamProto.Name}' because required factories/tech don't exist.");
                return;
            }
        }
        // The original prepends: required orders end up first, in reverse unit order.
        var orders = new List<WorkOrder>();
        foreach (var unitInfo in teamProto.TemplateInfo.Units)
            if (game.Templates.Find(unitInfo.UnitThingName) is { } thing && unitInfo.MaxUnits - unitInfo.MinUnits > 0)
                orders.Insert(0, new WorkOrder { Thing = thing, NumRequired = unitInfo.MaxUnits - unitInfo.MinUnits });
        foreach (var unitInfo in teamProto.TemplateInfo.Units)
            if (game.Templates.Find(unitInfo.UnitThingName) is { } thing)
                orders.Insert(0, new WorkOrder { Thing = thing, NumRequired = unitInfo.MinUnits, Required = true });
        if (orders.Count == 0)
            return;
        var team = new TeamInQueue { PriorityBuild = priorityBuild, FrameStarted = game.Frame };
        team.WorkOrders.AddRange(orders);
        if (priorityBuild)
            teamBuildQueue.Insert(0, team);
        else
            teamBuildQueue.Add(team);
        team.Team = game.Teams.CreateInactiveTeam(teamProto.Name);
        Debug(teamProto.Name + " - starting team build.");
        teamDelay = 0;
        RunExecuteActions(team.Team);
    }

    void RunExecuteActions(Team team)
    {
        var info = team.Prototype.TemplateInfo;
        if (info.ExecuteActions && Scripts?.FindScript(info.ProductionCondition) is { Actions.Count: > 0 } script)
            Scripts.ExecuteActionsFor(script.Actions, team);
    }

    /// <summary>RECRUIT_TEAM: forms the team from units the player already has near its home.</summary>
    public virtual void RecruitSpecificAITeam(TeamPrototype? teamProto, float recruitRadius)
    {
        if (recruitRadius < 1)
            recruitRadius = 99999;
        if (teamProto == null)
            return;
        if (teamProto.IsSingleton && game.Teams.FindTeam(teamProto.Name) is { } singleton && singleton.HasAnyObjects())
        {
            Debug("Unable to recruit singleton team '' because team already exists.");
            return;
        }
        if (!teamProto.TemplateInfo.HasHomeLocation && !IsSkirmishAI)
            Debug($"Error : team '{teamProto.Name}' has no Home Position (or Origin).");
        var theTeam = game.Teams.CreateInactiveTeam(teamProto.Name);
        Debug(teamProto.Name + " - Recruiting.");
        int unitsRecruited = 0;
        var home = teamProto.TemplateInfo.HomeLocation;
        foreach (var unitInfo in teamProto.TemplateInfo.Units)
        {
            if (game.Templates.Find(unitInfo.UnitThingName) is not { } thing)
                continue;
            for (int count = unitInfo.MaxUnits; count > 0; count--)
            {
                var unit = theTeam.TryToRecruit(thing, home, recruitRadius);
                if (unit == null)
                    break;
                unitsRecruited++;
                Debug($"Team '{teamProto.Name}' recruits {thing.Name} from team '{unit.Team?.Prototype.Name}'");
                unit.SetTeam(theTeam);
                unit.AI?.MoveTo(home, CommandSource.AI);
            }
        }
        if (unitsRecruited > 0)
        {
            teamReadyQueue.Insert(0, new TeamInQueue { FrameStarted = game.Frame, Team = theTeam });
            Debug(teamProto.Name + " - Finished recruiting.");
        }
        else
        {
            if (!teamProto.IsSingleton)
                game.Teams.DeleteTeam(theTeam);
            Debug(teamProto.Name + " - Recruited 0 units, disbanding.");
        }
    }

    protected virtual void ProcessTeamBuilding()
    {
        if (SelectTeamToBuild())
            QueueUnits();
    }

    /// <summary>Each queued order recruits idle units it can, and has the rest trained.</summary>
    protected void QueueUnits()
    {
        QueueSupplyTruck();
        foreach (var team in teamBuildQueue.ToList())
        {
            if (team.Team == null)
                continue;
            foreach (var order in team.WorkOrders)
            {
                var home = team.Team.Prototype.TemplateInfo.HomeLocation;
                bool hasHome = team.Team.Prototype.TemplateInfo.HasHomeLocation || GetBaseCenter(out home);
                while (order.IsWaitingToBuild)
                {
                    var unit = team.Team.TryToRecruit(order.Thing, home, Data.MaxRecruitRadius);
                    if (unit == null)
                        break;
                    order.NumCompleted++;
                    Debug($"Team '{team.Team.Prototype.Name}' recruits {order.Thing.Name} from team '{unit.Team?.Prototype.Name}'");
                    unit.SetTeam(team.Team);
                    if (hasHome)
                        unit.AI?.MoveTo(home, CommandSource.AI);
                    else
                        unit.AI?.Stop(CommandSource.AI);
                }
                if (order.IsWaitingToBuild)
                    StartTraining(order, team.PriorityBuild, team.Team.Name);
                else
                    order.ValidateFactory(player);
            }
        }
    }

    protected virtual void DoBaseBuilding()
    {
        if (!player.CanBuildBase)
            return;
        if (!readyToBuildStructure && --structureTimer <= 0)
        {
            readyToBuildStructure = true;
            buildDelay = 0;
        }
        if (--buildDelay < 1)
        {
            if (readyToBuildStructure)
                ProcessBaseBuilding();
            if (buildDelay < 1)
                buildDelay = 2 * LogicTime.FramesPerSecond;
        }
    }

    /// <summary>Teams waiting to go are released when everyone is idle (or after a minute).</summary>
    protected virtual void CheckReadyTeams()
    {
        foreach (var team in teamReadyQueue.ToList())
        {
            if (team.Team == null)
            {
                teamReadyQueue.Remove(team);
                continue;
            }
            bool timeExpired = team.FrameStarted + 60 * LogicTime.FramesPerSecond < game.Frame;
            bool allIdle = true, anyIdle = false;
            if (team.Reinforcement)
            {
                if (game.FindObject(team.ReinforcementId)?.AI is { } ai)
                    anyIdle = allIdle = ai.IsIdle;
            }
            else
            {
                allIdle = team.Team.IsIdle;
                anyIdle = team.Team.AllMembers.Any(o => o.AI is { IsIdle: true });
            }
            var info = team.Team.Prototype.TemplateInfo;
            if (anyIdle && info.ExecuteActions && Scripts?.FindScript(info.ProductionCondition) is { Actions.Count: > 0 })
                allIdle = true;
            if (timeExpired)
                allIdle = true;
            if (!allIdle)
                continue;
            team.SentToStartLocation = true;
            teamReadyQueue.Remove(team);
            if (!team.Reinforcement)
            {
                team.Team.SetActive();
                if (IsSkirmishAI)
                    Scripts?.ClearTeamFlags();
            }
            team.Release();
        }
    }

    /// <summary>Teams that are built go to the ready queue; teams out of build time go if they have the minimum, else disband.</summary>
    protected virtual void CheckQueuedTeams()
    {
        foreach (var team in teamBuildQueue.ToList())
        {
            if (team.Team == null || !team.IsBuildTimeExpired(game.Frame))
                continue;
            if (team.IsMinimumBuilt)
            {
                if (team.AreBuildsComplete)
                {
                    teamBuildQueue.Remove(team);
                    teamReadyQueue.Insert(0, team);
                }
            }
            else
            {
                teamBuildQueue.Remove(team);
                team.Disband(Scripts);
                team.Release();
                if (IsSkirmishAI)
                    Scripts?.ClearTeamFlags();
            }
        }
        foreach (var team in teamBuildQueue.ToList())
        {
            if (team.Team == null)
                continue;
            if (team.IsAllBuilt)
            {
                teamBuildQueue.Remove(team);
                teamReadyQueue.Insert(0, team);
                continue;
            }
            if (team.Team.AllMembers.Any(o => o.AI is { IsIdle: true }))
                RunExecuteActions(team.Team);
        }
    }

    protected virtual void DoTeamBuilding()
    {
        if (!player.CanBuildUnits)
            return;
        if (!readyToBuildTeam && --teamTimer <= 0)
        {
            readyToBuildTeam = true;
            teamDelay = 0;
        }
        if (--teamDelay < 1)
        {
            QueueUnits();
            if (readyToBuildTeam)
                ProcessTeamBuilding();
            teamDelay = 5 * LogicTime.FramesPerSecond;
        }
    }

    /// <summary>Spends general's points on the side's skill set (a skirmish AI picks one of them at random).</summary>
    void DoUpgradesAndSkills()
    {
        if (game.Frame < 2 || player.SciencePurchasePoints <= 0)
            return;
        if (Data.SideInfo(player.Side) is not { } sideInfo)
            return;
        if (skillsetSelector == InvalidSkillsetSelection)
        {
            int limit = 0;
            for (int i = 1; i < sideInfo.SkillSets.Count && sideInfo.SkillSets[i].Count > 0; i++)
                limit = i;
            skillsetSelector = IsSkirmishAI ? game.Random.Int(0, limit) : 0;
        }
        if (sideInfo.SkillSets.Count == 0)
            return;
        var skillset = sideInfo.SkillSets[Math.Clamp(skillsetSelector, 0, sideInfo.SkillSets.Count - 1)];
        foreach (string science in skillset)
            if (player.IsCapableOfPurchasingScience(science) && player.AttemptToPurchaseScience(science))
                Debug($"{player.Name} purchases from SkillSet{skillsetSelector + 1} {science}.");
    }

    public virtual void Update()
    {
        DoBaseBuilding();
        CheckReadyTeams();
        CheckQueuedTeams();
        DoTeamBuilding();
        DoUpgradesAndSkills();
        UpdateBridgeRepair();
    }

    /// <summary>The map is loaded: the player's factories join its build list, and "initially built" structures appear.</summary>
    public virtual void NewMap()
    {
        var mapList = player.BuildList.ToList();
        foreach (var obj in game.Objects.ToList())
            if (obj.Owner == player && obj.FindModule<ProductionUpdate>() != null)
                player.AddToBuildList(obj);
        ComputeCenterAndRadiusOfBase();
        foreach (var info in mapList)
        {
            if (info.TemplateName.Length == 0 || game.Templates.Find(info.TemplateName) is not { } plan)
                continue;
            if (info.IsInitiallyBuilt)
                BuildStructureNow(plan, info);
            else
                info.IncrementNumRebuilds();
        }
    }

    protected void ComputeCenterAndRadiusOfBase()
    {
        var total = Vector2.Zero;
        int numBldg = 0;
        foreach (var info in BuildList)
        {
            if (info.TemplateName.Length == 0 || game.Templates.Find(info.TemplateName) == null)
                continue;
            total += new Vector2(info.Location.X, info.Location.Y);
            numBldg++;
        }
        if (numBldg > 0)
            total /= numBldg;
        baseCenterSet = numBldg > 0;
        baseCenter = new Vector3(total, 0);
        float maxRadSqr = 0;
        foreach (var info in BuildList)
        {
            if (info.TemplateName.Length == 0 || game.Templates.Find(info.TemplateName) is not { } plan)
                continue;
            float dx = MathF.Abs(info.Location.X - baseCenter.X), dy = MathF.Abs(info.Location.Y - baseCenter.Y);
            float bldgRadius = plan.Geometry.BoundingCircleRadius * 0.4f;
            dx += bldgRadius;
            dy += bldgRadius;
            maxRadSqr = Math.Max(maxRadSqr, dx * dx + dy * dy);
        }
        baseRadius = MathF.Sqrt(maxRadSqr);
    }

    bool DozerInQueue() => teamBuildQueue.Any(t => t.IncludesADozer);

    protected virtual void QueueDozer()
    {
        if (DozerInQueue())
            return;
        bool canBuildUnits = player.CanBuildUnits;
        player.CanBuildUnits = true;
        foreach (var template in AllTemplates())
        {
            if (!template.Is("DOZER"))
                continue;
            var factory = FindFactory(template, true);
            if (factory == null)
                continue;
            var order = new WorkOrder { Thing = template, NumRequired = 1, Required = true };
            var team = new TeamInQueue { PriorityBuild = true, FrameStarted = game.Frame, Team = player.DefaultTeam };
            team.WorkOrders.Add(order);
            teamBuildQueue.Insert(0, team);
            Debug("DOZER - building one at the " + factory.Template.Name);
            teamDelay = 0;
            StartTraining(order, team.PriorityBuild, team.Team.Name);
            break;
        }
        player.CanBuildUnits = canBuildUnits;
    }

    /// <summary>The nearest free dozer (busy ones only if no other); asks for one to be built if the player has none.</summary>
    protected virtual GameObject? FindDozer(Vector3 pos)
    {
        GameObject? dozer = null, closestDozer = null;
        bool needDozer = true;
        float closestDistSqr = 0;
        foreach (var obj in game.Objects)
        {
            if (obj.Owner != player || !obj.IsKindOf(Kind.Dozer) || obj.AI is not DozerAIUpdate dozerAI)
                continue;
            bool anyTask = dozerAI.Task != DozerAIUpdate.DozerTask.None;
            if (!anyTask && Gatherer(obj) is { } gatherer && (gatherer.IsCurrentlyFerryingSupplies || gatherer.ForceWanting))
                continue;
            if (obj.Id == repairDozer)
                continue;
            needDozer = false;
            if (dozerAI.Task == DozerAIUpdate.DozerTask.Build)
                continue;
            if (!anyTask)
                dozer = obj;
            dozer ??= obj;
            if (!anyTask)
            {
                float dx = pos.X - dozer.Position.X, dy = pos.Y - dozer.Position.Y;
                float distSqr = dx * dx + dy * dy;
                if (closestDozer == null || distSqr < closestDistSqr)
                {
                    closestDozer = dozer;
                    closestDistSqr = distSqr;
                }
            }
        }
        if (needDozer)
            QueueDozer();
        return closestDozer ?? dozer;
    }

    /// <summary>
    /// The box around a player's structures (the original's getPlayerStructureBounds); all zero when it
    /// has none.
    /// </summary>
    public (Vector2 Lo, Vector2 Hi) GetPlayerStructureBounds(int playerNdx, bool conservative = false)
    {
        if (playerNdx < 0 || playerNdx >= game.Players.All.Count)
            return default;
        bool first = true;
        Vector2 lo = default, hi = default;
        foreach (var obj in game.Players.All[playerNdx].Teams.ToList().SelectMany(t => t.AllMembers))
        {
            if (!obj.IsKindOf(Kind.Structure))
                continue;
            if (conservative && obj.IsKindOf("CONSERVATIVE_BUILDING"))
                continue;
            var p = new Vector2(obj.Position.X, obj.Position.Y);
            if (first)
            {
                lo = hi = p;
                first = false;
            }
            else
            {
                lo = Vector2.Min(lo, p);
                hi = Vector2.Max(hi, p);
            }
        }
        return (lo, hi);
    }
}
