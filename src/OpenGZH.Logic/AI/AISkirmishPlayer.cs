using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.AI;

/// <summary>
/// The skirmish computer player (the original's AISkirmishPlayer): lays out its base from AIData's
/// SkirmishBuildList around its command center, picks the nearest enemy that is still in good shape,
/// builds base defenses on the Center/Flank/Backdoor approach paths, keeps its power up and repairs
/// broken bridges on the way to the enemy. Its skirmish scripts decide what to build and when.
/// </summary>
public sealed class AISkirmishPlayer : AIPlayer
{
    public const string SkirmishCenter = "Center";
    public const string SkirmishFlank = "Flank";
    public const string SkirmishBackdoor = "Backdoor";
    const float HugeDist = 1000000f;

    int curFrontBaseDefense, curFlankBaseDefense;
    float curFrontLeftDefenseAngle, curFrontRightDefenseAngle;
    float curLeftFlankLeftDefenseAngle, curLeftFlankRightDefenseAngle;
    float curRightFlankLeftDefenseAngle, curRightFlankRightDefenseAngle;
    int frameToCheckEnemy;
    Player? currentEnemy;

    public AISkirmishPlayer(Player p) : base(p)
    {
        frameLastBuildingBuilt = game.Frame;
        p.CanBuildUnits = true;
    }

    public override bool IsSkirmishAI => true;

    /// <summary>
    /// The original's skirmish base building: priority (script-requested) structures first; a power
    /// plant when short of power. (As in the original, the "can the dozer make it" test looks at the
    /// structure picked so far, so automatic ones only go up behind a script request.)
    /// </summary>
    protected override void ProcessBaseBuilding()
    {
        if (!readyToBuildStructure)
            return;
        ObjectTemplate? bldgPlan = null, powerPlan = null;
        BuildListInfo? bldgInfo = null, powerInfo = null;
        bool isPriority = false;
        bool isUnderPowered = !player.Energy.HasSufficientPower;
        bool powerUnderConstruction = false;
        foreach (var info in player.BuildList.ToList())
        {
            string name = info.TemplateName;
            if (name.Length == 0 || game.Templates.Find(name) is not { } curPlan)
                continue;
            var bldg = game.FindObject(info.ObjectId);
            if (info.ObjectId != 0)
            {
                if (bldg == null)
                {
                    info.ObjectId = 0;
                    info.ObjectTimestamp = game.Frame + 1;
                }
                else if (bldg.Owner == player)
                {
                    if (bldg.IsUnderConstruction)
                    {
                        if (bldg.IsKindOf("FS_POWER") && !bldg.IsKindOf("CASH_GENERATOR"))
                            powerUnderConstruction = true;
                        var myDozer = game.FindObject(bldg.BuilderId);
                        if (myDozer != null && (myDozer.Owner != player || myDozer.IsDisabledBy("DISABLED_UNMANNED")))
                        {
                            myDozer = null;
                            bldg.BuilderId = 0;
                        }
                        if (myDozer == null)
                        {
                            QueueDozer();
                            myDozer = FindDozer(bldg.Position);
                            if (myDozer?.AI == null)
                                continue;
                        }
                        ResumeConstruction(myDozer, bldg);
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
            if (bldg != null)
                continue;
            if (!IsLocationSafe(info.Location, curPlan))
            {
                if (Scripting.ScriptEngine.TraceScripts && info.IsPriorityBuild)
                    Log.Info($"[ai] {game.Frame} {curPlan.Name} location not safe");
                continue;
            }
            if (info.IsPriorityBuild && !isPriority)
            {
                bldgPlan = curPlan;
                bldgInfo = info;
                isPriority = true;
            }
            if (curPlan.Is("FS_POWER") && powerPlan == null && !curPlan.Is("CASH_GENERATOR") && (isUnderPowered || info.IsAutomaticBuild))
            {
                powerPlan = curPlan;
                powerInfo = info;
            }
            if (!info.IsAutomaticBuild)
                continue;
            var dozer = FindDozer(info.Location);
            if (dozer == null)
            {
                if (isUnderPowered)
                    QueueDozer();
                continue;
            }
            if (!game.Building.CanMakeUnit(dozer, bldgPlan))
                continue;
            if (info.IsBuildable && bldgPlan == null)
            {
                bldgPlan = curPlan;
                bldgInfo = info;
            }
        }
        if (powerPlan != null && powerInfo != null && (bldgPlan == null || !powerPlan.IsEquivalentTo(bldgPlan)) && !powerUnderConstruction)
        {
            bldgPlan = powerPlan;
            bldgInfo = powerInfo;
        }
        if (Scripting.ScriptEngine.TraceScripts)
            Log.Info($"[ai] {game.Frame} {player.Name} base building: plan={bldgPlan?.Name} power={powerPlan?.Name} underpowered={isUnderPowered}");
        if (bldgPlan == null || bldgInfo == null)
            return;
        var built = BuildStructureWithDozer(bldgPlan, bldgInfo);
        if (Scripting.ScriptEngine.TraceScripts)
            Log.Info($"[ai] {game.Frame} {player.Name} build {bldgPlan.Name} at {bldgInfo.Location}: {(built == null ? "failed" : "started")}");
        if (built != null)
        {
            bldgInfo.ObjectId = built.Id;
            bldgInfo.DecrementNumRebuilds();
            StructureBuilt();
        }
    }

    /// <summary>SKIRMISH_BUILD_BUILDING: marks the first unbuilt build list entry of that type as a priority build.</summary>
    public override void BuildSpecificAIBuilding(string thingName)
    {
        bool found = false, foundUnbuilt = false;
        foreach (var info in player.BuildList)
        {
            if (info.TemplateName != thingName || game.Templates.Find(info.TemplateName) == null)
                continue;
            found = true;
            if (game.FindObject(info.ObjectId) != null || info.IsPriorityBuild)
                continue;
            foundUnbuilt = true;
            info.MarkPriorityBuild();
            break;
        }
        if (foundUnbuilt)
        {
            buildDelay = 0;
            Scripts?.AppendDebugMessage($"Queueing building '{thingName}' for construction.", false);
        }
        else if (found)
            Scripts?.AppendDebugMessage($"Warning - all instances of building '{thingName}' are already built or queued for build, not queueing.", false);
        else
            Scripts?.AppendDebugMessage($"Error - could not find building '{thingName}' in the building template list.", false);
    }

    int MyEnemyPlayerIndex()
    {
        if (currentEnemy != null)
            return currentEnemy.Index;
        int i;
        for (i = 0; i < game.Players.All.Count; i++)
            if (game.Players.All[i].IsHuman)
                break;
        return i;
    }

    /// <summary>
    /// The nearest enemy base, avoiding enemies other computer players are already after and favouring
    /// whoever is after us; one with no units or no factory left is a last resort.
    /// </summary>
    void AcquireEnemy()
    {
        Player? bestEnemy = null;
        float bestDistanceSqr = HugeDist * HugeDist;
        if (currentEnemy != null && currentEnemy.HasAnyUnits() && currentEnemy.HasAnyBuildFacility())
            return;
        var players = game.Players.All;
        for (int i = 0; i < players.Count; i++)
        {
            var cur = players[i];
            if (player.RelationshipTo(cur.DefaultTeam) != Relationship.Enemies || !cur.HasAnyObjects())
                continue;
            bool inBadShape = !cur.HasAnyUnits() || !cur.HasAnyBuildFacility();
            var bounds = GetPlayerStructureBounds(i);
            var enemyPos = new Vector2(bounds.Lo.X + (bounds.Hi.X - bounds.Lo.X) / 2, bounds.Lo.Y + (bounds.Hi.Y - bounds.Lo.Y) / 2);
            float curDistSqr = Sqr(enemyPos.X - baseCenter.X) + Sqr(enemyPos.Y - baseCenter.Y);
            if (inBadShape)
                curDistSqr = HugeDist * HugeDist * 0.5f;
            for (int k = 0; k < players.Count; k++)
            {
                if (k == i)
                    continue;
                var some = players[k];
                if (some.IsSkirmishAIPlayer && some.CurrentEnemy == cur)
                    curDistSqr += 500 * 500;
                if (some.IsSkirmishAIPlayer && some.CurrentEnemy == player)
                    curDistSqr = Math.Max(0, curDistSqr - 25 * 25);
            }
            if (curDistSqr < bestDistanceSqr)
            {
                bestEnemy = cur;
                bestDistanceSqr = curDistSqr;
            }
        }
        if (bestEnemy != null && bestEnemy != currentEnemy)
        {
            currentEnemy = bestEnemy;
            Scripts?.AppendDebugMessage($"{player.Name} acquiring target enemy player: {currentEnemy.Name}", false);
        }
    }

    static float Sqr(float v) => v * v;

    /// <summary>The current enemy, re-chosen every five seconds.</summary>
    public override Player? GetAiEnemy()
    {
        if (game.Frame >= frameToCheckEnemy)
        {
            frameToCheckEnemy = game.Frame + 5 * LogicTime.FramesPerSecond;
            AcquireEnemy();
        }
        return currentEnemy;
    }

    public override void BuildAIBaseDefense(bool flank)
    {
        if (Data.SideInfo(player.Side) is { } side)
            BuildAIBaseDefenseStructure(side.BaseDefenseStructure1, flank);
    }

    /// <summary>
    /// Places a defense on the base perimeter towards an approach path (Center, or alternately Flank and
    /// Backdoor), working outwards left and right of the path by four structure radii each time, up to 60°.
    /// </summary>
    public override void BuildAIBaseDefenseStructure(string thingName, bool flank)
    {
        if (game.Templates.Find(thingName) is not { } template)
        {
            Log.Warning($"Couldn't find base defense structure '{thingName}' for side {player.Side}");
            return;
        }
        while (true)
        {
            string pathLabel = flank
                ? ((curFlankBaseDefense & 1) != 0 ? SkirmishFlank : SkirmishBackdoor) + (player.MpStartIndex + 1)
                : SkirmishCenter + (player.MpStartIndex + 1);
            var goalPos = baseCenter;
            if (game.Waypoints?.ClosestOnPath(goalPos, pathLabel) is { } way)
                goalPos = way.Position;
            else
            {
                if (flank)
                    return;
                var bounds = GetPlayerStructureBounds(MyEnemyPlayerIndex());
                goalPos.X = bounds.Lo.X + (bounds.Hi.X - bounds.Lo.X) / 2;
                goalPos.Y = bounds.Lo.Y + (bounds.Hi.Y - bounds.Lo.Y) / 2;
            }
            var offset = new Vector2(goalPos.X - baseCenter.X, goalPos.Y - baseCenter.Y);
            if (offset.LengthSquared() > 0)
                offset = Vector2.Normalize(offset);
            float defenseDistance = baseRadius + Data.SkirmishBaseDefenseExtraDistance;
            offset *= defenseDistance;
            float structureRadius = template.Geometry.BoundingCircleRadius;
            float baseCircumference = 2 * MathF.PI * defenseDistance;
            float angleOffset = 2 * MathF.PI * (structureRadius * 4 / baseCircumference);
            float angle;
            if (flank)
            {
                int selector = curFlankBaseDefense >> 1;
                if ((curFlankBaseDefense & 1) != 0)
                {
                    if ((selector & 1) != 0) { curLeftFlankRightDefenseAngle -= angleOffset; angle = curLeftFlankRightDefenseAngle; }
                    else { angle = curLeftFlankLeftDefenseAngle; curLeftFlankLeftDefenseAngle += angleOffset; }
                }
                else
                {
                    if ((selector & 1) != 0) { curRightFlankRightDefenseAngle -= angleOffset; angle = curRightFlankRightDefenseAngle; }
                    else { angle = curRightFlankLeftDefenseAngle; curRightFlankLeftDefenseAngle += angleOffset; }
                }
            }
            else
            {
                if ((curFrontBaseDefense & 1) != 0) { curFrontRightDefenseAngle -= angleOffset; angle = curFrontRightDefenseAngle; }
                else { angle = curFrontLeftDefenseAngle; curFrontLeftDefenseAngle += angleOffset; }
            }
            if (angle > MathF.PI / 3)
                break;
            float s = MathF.Sin(angle), c = MathF.Cos(angle);
            var buildPos = baseCenter;
            buildPos.X += offset.X * c - offset.Y * s;
            buildPos.Y += offset.Y * c + offset.X * s;
            float placeAngle = template.PlacementViewAngle;
            bool canBuild = game.Building.CheckLocation(template, buildPos, placeAngle, player) == Construction.BuildLocation.Ok;
            if (flank)
                curFlankBaseDefense++;
            else
                curFrontBaseDefense++;
            if (canBuild)
            {
                player.AddToPriorityBuildList(thingName, buildPos, placeAngle);
                break;
            }
        }
    }

    /// <summary>A waypoint on the way can't be reached: look for a broken bridge between and have it repaired.</summary>
    public override bool CheckBridges(GameObject unit, Waypoint way)
    {
        if (unit.AI is not { } ai)
            return false;
        var unitPos = unit.Position;
        var visited = new HashSet<Waypoint>();
        for (var cur = way; cur != null && visited.Add(cur); cur = cur.Next.Count > 0 ? cur.Next[0] : null)
        {
            if (game.Pathfinder.FindPath(unitPos, cur.Position, ai.Surfaces).Count > 0)
                continue;
            if (game.Pathfinder.FindBrokenBridge(unitPos, cur.Position, ai.Surfaces) is { } bridge)
            {
                RepairStructure(bridge.Id);
                return true;
            }
        }
        return false;
    }

    /// <summary>The skirmish recruit: the same as the campaign one, but always warns about a missing home.</summary>
    public override void RecruitSpecificAITeam(TeamPrototype? teamProto, float recruitRadius)
    {
        if (teamProto != null && !teamProto.TemplateInfo.HasHomeLocation)
            Scripts?.AppendDebugMessage($"Error : team '{teamProto.Name}' has no Home Position (or Origin).", false);
        base.RecruitSpecificAITeam(teamProto, recruitRadius);
    }

    /// <summary>Like the campaign AI, but never waits more than three seconds between structures.</summary>
    protected override void DoBaseBuilding()
    {
        if (!player.CanBuildBase)
            return;
        if (!readyToBuildStructure)
        {
            if (--structureTimer <= 0)
            {
                readyToBuildStructure = true;
                buildDelay = 0;
            }
            if (structureTimer > 3 * LogicTime.FramesPerSecond)
                structureTimer = 3 * LogicTime.FramesPerSecond;
        }
        if (--buildDelay < 1)
        {
            if (readyToBuildStructure)
                ProcessBaseBuilding();
            if (buildDelay < 1)
                buildDelay = 2 * LogicTime.FramesPerSecond;
        }
    }

    /// <summary>Like the campaign AI, but at most three seconds between teams and a check every two.</summary>
    protected override void DoTeamBuilding()
    {
        if (!player.CanBuildUnits)
            return;
        if (!readyToBuildTeam)
        {
            if (--teamTimer <= 0)
            {
                readyToBuildTeam = true;
                teamDelay = 0;
            }
            if (teamTimer > 3 * LogicTime.FramesPerSecond)
                teamTimer = 3 * LogicTime.FramesPerSecond;
        }
        if (--teamDelay < 1)
        {
            QueueUnits();
            if (readyToBuildTeam)
                ProcessTeamBuilding();
            teamDelay = 2 * LogicTime.FramesPerSecond;
        }
    }

    /// <summary>
    /// Moves the side's skirmish base layout to where the player's starting command center stands
    /// (which is removed: the build list builds it again as "initially built"). The layout is turned
    /// by 135° (plus a turn by map ninth with RotateSkirmishBases); as in the original, every entry is
    /// moved, because the test looks at the first entry (the command center).
    /// </summary>
    void AdjustBuildList(List<BuildListInfo> list)
    {
        Vector3? start = null;
        foreach (var obj in game.Objects.ToList())
        {
            if (obj.Owner == player && obj.IsKindOf("COMMANDCENTER"))
            {
                start = obj.Position;
                game.DestroyObject(obj);
                break;
            }
        }
        if (start is not { } startPos)
            return;
        var buildPos = Vector3.Zero;
        foreach (var cur in list)
            if (game.Templates.Find(cur.TemplateName) is { } t && t.Is("COMMANDCENTER"))
            {
                buildPos = cur.Location;
                cur.IsInitiallyBuilt = true;
            }
        float width = game.Terrain.WorldWidth, height = game.Terrain.WorldHeight;
        int gridIndex = 0;
        if (startPos.X > width / 3) gridIndex++;
        if (startPos.X > 2 * width / 3) gridIndex++;
        if (startPos.Y > height / 3) gridIndex += 3;
        if (startPos.Y > 2 * height / 3) gridIndex += 3;
        float angle = 0;
        if (Data.RotateSkirmishBases)
            angle = gridIndex switch
            {
                1 => MathF.PI / 4,
                2 => MathF.PI / 2,
                3 => -MathF.PI / 4,
                5 => 3 * MathF.PI / 4,
                6 => -MathF.PI / 2,
                7 => -3 * MathF.PI / 4,
                8 => MathF.PI,
                _ => 0,
            };
        angle += 3 * MathF.PI / 4;
        float s = MathF.Sin(angle), c = MathF.Cos(angle);
        bool firstIsCommandCenter = list.Count > 0 && game.Templates.Find(list[0].TemplateName) is { } first && first.Is("COMMANDCENTER");
        if (!firstIsCommandCenter)
            return;
        foreach (var cur in list)
        {
            var p = cur.Location - buildPos;
            float newX = p.X * c - p.Y * s;
            float newY = p.Y * c + p.X * s;
            cur.Location = new Vector3(newX + startPos.X, newY + startPos.Y, cur.Location.Z);
        }
    }

    /// <summary>The skirmish start: the side's base layout from AIData, then "initially built" structures appear.</summary>
    public override void NewMap()
    {
        if (Data.SkirmishBuildList(player.Side) is { } build)
        {
            var list = build.Select(BuildListInfo.From).ToList();
            AdjustBuildList(list);
            player.BuildList.Clear();
            player.BuildList.AddRange(list);
            ComputeCenterAndRadiusOfBase();
        }
        else
            Log.Warning($"Couldn't find build list for skirmish player side {player.Side}.");
        foreach (var info in player.BuildList.ToList())
        {
            if (info.TemplateName.Length == 0 || game.Templates.Find(info.TemplateName) is not { } plan)
                continue;
            if (info.IsInitiallyBuilt)
                BuildStructureNow(plan, info);
            else
                info.IncrementNumRebuilds();
        }
    }

    /// <summary>Cluster mines go on an approach path at the base edge; everything else on the most valuable spot.</summary>
    public override bool ComputeSuperweaponTarget(SpecialPowerTemplate power, out Vector3 retPos, int playerNdx, float weaponRadius)
    {
        if (power.IsType("SPECIAL_CLUSTER_MINES", "NUKE_SPECIAL_CLUSTER_MINES"))
        {
            int mode = game.Random.Int(0, 2);
            string pathLabel = (mode == 1 ? SkirmishFlank : mode == 2 ? SkirmishBackdoor : SkirmishCenter) + (player.MpStartIndex + 1);
            var goalPos = baseCenter;
            if (game.Waypoints?.ClosestOnPath(goalPos, pathLabel) is { } way)
                goalPos = way.Position;
            else
            {
                var bounds = GetPlayerStructureBounds(MyEnemyPlayerIndex());
                goalPos.X = bounds.Lo.X + (bounds.Hi.X - bounds.Lo.X) / 2;
                goalPos.Y = bounds.Lo.Y + (bounds.Hi.Y - bounds.Lo.Y) / 2;
            }
            var offset = new Vector2(goalPos.X - baseCenter.X, goalPos.Y - baseCenter.Y);
            if (offset.LengthSquared() > 0)
                offset = Vector2.Normalize(offset);
            offset *= baseRadius;
            retPos = baseCenter + new Vector3(offset, 0);
            retPos.Z = game.Terrain.GroundHeight(retPos);
            return true;
        }
        return base.ComputeSuperweaponTarget(power, out retPos, playerNdx, weaponRadius);
    }
}
