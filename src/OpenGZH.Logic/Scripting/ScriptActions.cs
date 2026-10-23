using System.Numerics;
using OpenGZH.Logic.AI;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.SpecialPowers;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.Scripting;

/// <summary>
/// Script actions (the original's ScriptActions), by their internal names. Actions that only affect
/// the camera, the screen or the interface are passed to the client through
/// <see cref="GameEvents.OnScriptClientAction"/>; the rest change the game here.
/// </summary>
public sealed partial class ScriptActions
{
    readonly GameLogic game;
    readonly ScriptEngine engine;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> reported = new();

    /// <summary>The original's REALLY_FAR.</summary>
    const float ReallyFar = 100000;
    const CommandSource FromScript = CommandSource.Script;

    public ScriptActions(GameLogic game, ScriptEngine engine)
    {
        this.game = game;
        this.engine = engine;
    }

    /// <summary>Action names that reached the end of the dispatcher without a handler.</summary>
    public static IEnumerable<string> Missing => reported.Keys;

    /// <summary>Per-frame work (the original's is empty).</summary>
    public void Update() { }

    // ---- Lookups ----

    GameObject? Unit(ScriptParam p) => engine.GetUnitNamed(p.String);
    Team? TeamNamed(ScriptParam p) => engine.GetTeamNamed(p.String);
    Player? PlayerNamed(ScriptParam p) => engine.GetPlayer(p.String);
    Waypoint? Way(string name) => game.Waypoints?.Find(name);
    TriggerArea? Area(ScriptParam p) => engine.GetQualifiedTriggerArea(p.String);
    CommandButton? Button(string name) => game.Catalog.CommandButtonTemplate(name);
    SpecialPowerTemplate? Power(string name) => game.Catalog.SpecialPower(name);

    /// <summary>The closest waypoint carrying this path label (the original's getClosestWaypointOnPath).</summary>
    Waypoint? ClosestOnPath(Vector3 pos, string label) => game.Waypoints?.ClosestOnPath(pos, label);

    void Client(ScriptAction action) => game.Events.ScriptClientAction(action);

    /// <summary>The original's updateTeamAndPlayerStuff: upgrades re-checked after a change of side.</summary>
    static void UpdateTeamAndPlayerStuff(GameObject obj) => obj.CheckUpgradeModules();

    /// <summary>Average position of a team's members (scripts that pick the closest waypoint of a path use it).</summary>
    static bool TeamAverage(Team team, out Vector3 pos)
    {
        pos = Vector3.Zero;
        int count = 0;
        foreach (var obj in team.AllMembers)
        {
            pos += obj.Position;
            count++;
        }
        if (count == 0)
            return false;
        pos /= count;
        return true;
    }

    // ---- The dispatcher (the original's executeAction) ----

    public void Execute(ScriptAction a)
    {
        switch (a.Name)
        {
            case "DEBUG_MESSAGE_BOX": engine.AppendDebugMessage(a[0].String, true); return;
            case "DEBUG_STRING": engine.AppendDebugMessage(a[0].String, false); return;
            case "DEBUG_CRASH_BOX": Log.Warning("Your Script requested the following message be displayed: " + a[0].String); return;
            case "DAMAGE_MEMBERS_OF_TEAM": TeamNamed(a[0])?.DamageTeamMembers(a[1].Real); return;
            case "MOVE_TEAM_TO": DoMoveToWaypoint(a[0], a[1].String); return;
            case "MOVE_NAMED_UNIT_TO": DoNamedMoveToWaypoint(a[0], a[1].String); return;
            case "TEAM_SET_STATE": if (TeamNamed(a[0]) is { } stateTeam) stateTeam.State = a[1].String; return;
            case "TEAM_FOLLOW_WAYPOINTS": DoTeamFollowWaypoints(a[0], a[1].String, a[2].Bool, exact: false); return;
            case "TEAM_FOLLOW_WAYPOINTS_EXACT": DoTeamFollowWaypoints(a[0], a[1].String, a[2].Bool, exact: true); return;
            case "NAMED_FOLLOW_WAYPOINTS_EXACT": DoNamedFollowWaypoints(a[0], a[1].String, exact: true); return;
            case "NAMED_FOLLOW_WAYPOINTS": DoNamedFollowWaypoints(a[0], a[1].String, exact: false); return;
            case "SKIRMISH_FOLLOW_APPROACH_PATH": DoTeamFollowSkirmishApproachPath(a[0], a[1].String, a[2].Bool); return;
            case "SKIRMISH_MOVE_TO_APPROACH_PATH": DoTeamMoveToSkirmishApproachPath(a[0], a[1].String); return;
            case "CREATE_REINFORCEMENT_TEAM": DoCreateReinforcements(a[0].String, a[1].String); return;
            case "SKIRMISH_BUILD_BUILDING": engine.CurrentPlayer?.BuildSpecificBuilding(a[0].String); return;
            case "AI_PLAYER_BUILD_SUPPLY_CENTER": PlayerNamed(a[0])?.BuildBySupplies(a[2].Int, a[1].String); return;
            case "AI_PLAYER_BUILD_TYPE_NEAREST_TEAM":
                if (PlayerNamed(a[0]) is { } nearestPlayer && engine.GetTeamNamed(a[2].String) is { } nearestTeam)
                    nearestPlayer.BuildSpecificBuildingNearestTeam(a[1].String, nearestTeam);
                return;
            case "AI_PLAYER_BUILD_UPGRADE": PlayerNamed(a[0])?.BuildUpgrade(a[1].String); return;
            case "SKIRMISH_BUILD_BASE_DEFENSE_FRONT": engine.CurrentPlayer?.BuildBaseDefense(false); return;
            case "SKIRMISH_BUILD_BASE_DEFENSE_FLANK": engine.CurrentPlayer?.BuildBaseDefense(true); return;
            case "SKIRMISH_BUILD_STRUCTURE_FRONT": engine.CurrentPlayer?.BuildBaseDefenseStructure(a[0].String, false); return;
            case "SKIRMISH_BUILD_STRUCTURE_FLANK": engine.CurrentPlayer?.BuildBaseDefenseStructure(a[0].String, true); return;
            case "RECRUIT_TEAM":
                if (game.Teams.FindTeamPrototype(a[0].String) is { } recruitProto)
                    recruitProto.ControllingPlayer.RecruitSpecificTeam(recruitProto, a[1].Real);
                return;
            case "BUILD_TEAM":
                if (game.Teams.FindTeamPrototype(a[0].String) is { } buildProto)
                    buildProto.ControllingPlayer.BuildSpecificTeam(buildProto);
                return;

            case "QUICKVICTORY": DoQuickVictory(); return;
            case "VICTORY": DoVictory(); return;
            case "DEFEAT": DoDefeat(); return;
            case "LOCALDEFEAT": DoLocalDefeat(); return;

            case "CREATE_OBJECT": DoCreateObject("", a[0].String, a[1].String, a[2].Coord, a[3].Real); return;
            case "UNIT_SPAWN_NAMED_LOCATION_ORIENTATION": DoCreateObject(a[0].String, a[1].String, a[2].String, a[3].Coord, a[4].Real); return;
            case "CREATE_NAMED_ON_TEAM_AT_WAYPOINT": CreateUnitOnTeamAt(a[0].String, a[1].String, a[2].String, a[3].String); return;
            case "CREATE_UNNAMED_ON_TEAM_AT_WAYPOINT": CreateUnitOnTeamAt("", a[0].String, a[1].String, a[2].String); return;

            case "TEAM_ATTACK_TEAM":
                if (TeamNamed(a[0]) is { } attackers && TeamNamed(a[1]) is { } victims)
                    AIGroup.FromTeam(attackers).GroupAttackTeam(victims, -1, FromScript);
                return;
            case "NAMED_ATTACK_NAMED":
                if (Unit(a[0]) is { AI: { } namedAttackAI } && Unit(a[1]) is { } namedVictim)
                {
                    namedAttackAI.SetLocomotorSet("SET_NORMAL");
                    namedAttackAI.AttackObject(namedVictim, FromScript);
                }
                return;
            case "NAMED_APPLY_ATTACK_PRIORITY_SET":
                if (Unit(a[0]) is { AI: { } prioAI })
                    prioAI.AttackInfo = engine.GetAttackInfo(a[1].String);
                return;
            case "TEAM_APPLY_ATTACK_PRIORITY_SET": UpdateTeamAttackPrioritySet(a[0], a[1].String); return;
            case "SET_BASE_CONSTRUCTION_SPEED": PlayerNamed(a[0])?.SetTeamDelaySeconds(a[1].Int); return;
            case "NAMED_SET_ATTITUDE": if (Unit(a[0]) is { AI: { } moodAI }) moodAI.Attitude = (Attitude)a[1].Int; return;
            case "TEAM_SET_ATTITUDE": if (TeamNamed(a[0]) is { } moodTeam) AIGroup.FromTeam(moodTeam).SetAttitude((Attitude)a[1].Int); return;
            case "NAMED_SET_REPULSOR": Unit(a[0])?.SetStatus("REPULSOR", a[1].Bool); return;
            case "TEAM_SET_REPULSOR":
                foreach (var obj in TeamNamed(a[0])?.AllMembers.ToList() ?? [])
                    obj.SetStatus("REPULSOR", a[1].Bool);
                return;
            case "NAMED_ATTACK_AREA":
                if (Unit(a[0]) is { AI: { } areaAI } && Area(a[1]) is { } attackArea)
                {
                    areaAI.SetLocomotorSet("SET_NORMAL");
                    areaAI.AttackArea(attackArea, FromScript);
                }
                return;
            case "NAMED_ATTACK_TEAM":
                if (Unit(a[0]) is { AI: { } teamAttackAI } && TeamNamed(a[1]) is { } attackedTeam)
                {
                    teamAttackAI.SetLocomotorSet("SET_NORMAL");
                    teamAttackAI.AttackTeam(attackedTeam, FromScript);
                }
                return;
            case "TEAM_ATTACK_AREA":
                if (TeamNamed(a[0]) is { } areaTeam && Area(a[1]) is { } teamArea)
                    AIGroup.FromTeam(areaTeam).GroupAttackArea(teamArea, FromScript);
                return;
            case "TEAM_ATTACK_NAMED":
                if (TeamNamed(a[0]) is { } namedAttackers && Unit(a[1]) is { } theVictim)
                    AIGroup.FromTeam(namedAttackers).GroupAttackObject(theVictim, -1, FromScript);
                return;
            case "TEAM_LOAD_TRANSPORTS": DoLoadAllTransports(a[0]); return;
            case "NAMED_ENTER_NAMED":
                if (Unit(a[0]) is { AI: { } enterAI } && Unit(a[1]) is { } transport)
                {
                    enterAI.SetLocomotorSet("SET_NORMAL");
                    enterAI.Enter(transport, FromScript);
                }
                return;
            case "TEAM_ENTER_NAMED":
                if (TeamNamed(a[0]) is { } enterTeam && Unit(a[1]) is { } enterTarget)
                    AIGroup.FromTeam(enterTeam).GroupEnter(enterTarget, FromScript);
                return;
            case "NAMED_EXIT_ALL":
                if (Unit(a[0]) is { AI: { } exitAI })
                {
                    exitAI.SetLocomotorSet("SET_NORMAL");
                    exitAI.Evacuate(FromScript);
                }
                return;
            case "TEAM_EXIT_ALL": if (TeamNamed(a[0]) is { } exitTeam) AIGroup.FromTeam(exitTeam).GroupEvacuate(FromScript); return;
            case "NAMED_SET_EVAC_LEFT_OR_RIGHT": if (Unit(a[0])?.Contain is { } evacContain) evacContain.EvacDisposition = a[1].Int; return;

            case "NAMED_GUARD":
                if (Unit(a[0]) is { AI: { } guardAI } guardUnit)
                {
                    guardAI.SetLocomotorSet("SET_NORMAL");
                    guardAI.Guard(guardUnit.Position, FromScript);
                }
                return;
            case "TEAM_GUARD":
                foreach (var obj in TeamNamed(a[0])?.AllMembers.ToList() ?? [])
                    obj.AI?.Guard(obj.Position, FromScript);
                return;
            case "TEAM_GUARD_POSITION":
                if (Way(a[1].String) is { } guardWay && TeamNamed(a[0]) is { } guardTeam)
                    AIGroup.FromTeam(guardTeam).GroupGuardPosition(guardWay.Position, FromScript);
                return;
            case "TEAM_GUARD_OBJECT":
                if (Unit(a[1]) is { } guarded && TeamNamed(a[0]) is { } guardObjTeam)
                    AIGroup.FromTeam(guardObjTeam).GroupGuardObject(guarded, FromScript);
                return;
            case "TEAM_GUARD_AREA":
                if (Area(a[1]) is { } guardArea && TeamNamed(a[0]) is { } guardAreaTeam)
                    AIGroup.FromTeam(guardAreaTeam).GroupGuardArea(guardArea, FromScript);
                return;
            case "TEAM_GUARD_SUPPLY_CENTER":
                if (TeamNamed(a[0]) is { } supplyTeam)
                    supplyTeam.ControllingPlayer.GuardSupplyCenter(supplyTeam, a[1].Int);
                return;
            case "TEAM_GUARD_IN_TUNNEL_NETWORK":
                foreach (var obj in TeamNamed(a[0])?.AllMembers.ToList() ?? [])
                    obj.AI?.GuardTunnelNetwork(FromScript);
                return;
            case "NAMED_HUNT":
                if (Unit(a[0]) is { AI: { } huntAI })
                {
                    huntAI.SetLocomotorSet("SET_NORMAL");
                    huntAI.Hunt(FromScript);
                }
                return;
            case "TEAM_HUNT": if (TeamNamed(a[0]) is { } huntTeam) AIGroup.FromTeam(huntTeam).GroupHunt(FromScript); return;
            case "TEAM_HUNT_WITH_COMMAND_BUTTON": DoTeamHuntWithCommandButton(a[0], a[1].String); return;
            case "PLAYER_HUNT": PlayerNamed(a[0])?.SetUnitsShouldHunt(true, FromScript); return;
            case "PLAYER_SELL_EVERYTHING": PlayerNamed(a[0])?.SellEverythingUnderTheSun(); return;
            case "PLAYER_DISABLE_BASE_CONSTRUCTION": if (PlayerNamed(a[0]) is { } p1) p1.CanBuildBase = false; return;
            case "PLAYER_ENABLE_BASE_CONSTRUCTION": if (PlayerNamed(a[0]) is { } p2) p2.CanBuildBase = true; return;
            case "PLAYER_DISABLE_UNIT_CONSTRUCTION": if (PlayerNamed(a[0]) is { } p3) p3.CanBuildUnits = false; return;
            case "PLAYER_ENABLE_UNIT_CONSTRUCTION": if (PlayerNamed(a[0]) is { } p4) p4.CanBuildUnits = true; return;
            case "PLAYER_DISABLE_FACTORIES": PlayerNamed(a[0])?.SetObjectsEnabled(a[1].String, false); return;
            case "PLAYER_ENABLE_FACTORIES": PlayerNamed(a[0])?.SetObjectsEnabled(a[1].String, true); return;
            case "PLAYER_REPAIR_NAMED_STRUCTURE":
                if (PlayerNamed(a[0]) is { } repairPlayer && Unit(a[1]) is { } structure)
                    repairPlayer.RepairStructure(structure.Id);
                return;

            case "NAMED_DAMAGE":
                Unit(a[0])?.Damage(new Combat.DamageInfo { Amount = a[1].Int, DamageType = "UNRESISTABLE", DeathType = "NORMAL" });
                return;
            case "NAMED_DELETE": if (Unit(a[0]) is { } deleted) game.DestroyObject(deleted); return;
            case "TEAM_DELETE": TeamNamed(a[0])?.DeleteTeam(false); return;
            case "TEAM_DELETE_LIVING": TeamNamed(a[0])?.DeleteTeam(true); return;
            case "TEAM_WANDER": DoTeamWander(a[0], a[1].String, panic: false); return;
            case "TEAM_PANIC": DoTeamWander(a[0], a[1].String, panic: true); return;
            case "TEAM_WANDER_IN_PLACE":
                foreach (var obj in TeamNamed(a[0])?.AllMembers.ToList() ?? [])
                    obj.AI?.WanderInPlace(FromScript);
                return;
            case "TEAM_INCREASE_PRIORITY":
                if (TeamNamed(a[0])?.Prototype is { } incProto)
                {
                    incProto.IncreaseAIPriorityForSuccess();
                    engine.AppendDebugMessage($"Team '{a[0].String}' priority increased to {incProto.TemplateInfo.ProductionPriority} for success.", false);
                }
                return;
            case "TEAM_DECREASE_PRIORITY":
                if (TeamNamed(a[0])?.Prototype is { } decProto)
                {
                    decProto.DecreaseAIPriorityForFailure();
                    engine.AppendDebugMessage($"Team '{a[0].String}' priority decreased to {decProto.TemplateInfo.ProductionPriority} for failure.", false);
                }
                return;
            case "NAMED_KILL": Unit(a[0])?.Kill(); return;
            case "TEAM_KILL": TeamNamed(a[0])?.KillTeam(); return;
            case "PLAYER_KILL": PlayerNamed(a[0])?.KillPlayer(); return;

            case "PLAYER_TRANSFER_OWNERSHIP_PLAYER":
                if (PlayerNamed(a[1]) is { } dest && PlayerNamed(a[0]) is { } src)
                    dest.TransferAssetsFromThat(src);
                return;
            case "NAMED_TRANSFER_OWNERSHIP_PLAYER":
                if (Unit(a[0]) is { } transferred && PlayerNamed(a[1]) is { } newOwner)
                {
                    transferred.SetTeam(newOwner.DefaultTeam);
                    UpdateTeamAndPlayerStuff(transferred);
                }
                return;
            case "TEAM_TRANSFER_TO_PLAYER":
                if (TeamNamed(a[0]) is { } transferTeam && PlayerNamed(a[1]) is { } toPlayer)
                {
                    transferTeam.SetControllingPlayer(toPlayer);
                    foreach (var obj in transferTeam.AllMembers.ToList())
                        UpdateTeamAndPlayerStuff(obj);
                }
                return;
            case "PLAYER_EXCLUDE_FROM_SCORE_SCREEN": if (PlayerNamed(a[0]) is { } excluded) excluded.ListInScoreScreen = false; return;
            case "ENABLE_SCORING": game.IsScoringEnabled = true; return;
            case "DISABLE_SCORING": game.IsScoringEnabled = false; return;
            case "PLAYER_RELATES_PLAYER":
                if (PlayerNamed(a[2]) is { } relDest && PlayerNamed(a[0]) is { } relSrc)
                    relSrc.SetRelationship(relDest, (Relationship)a[1].Int);
                return;

            case "NAMED_SET_STEALTH_ENABLED": Unit(a[0])?.SetScriptStatus("SCRIPT_UNSTEALTHED", !a[1].Bool); return;
            case "TEAM_SET_STEALTH_ENABLED":
                foreach (var obj in TeamNamed(a[0])?.AllMembers.ToList() ?? [])
                    obj.SetScriptStatus("SCRIPT_UNSTEALTHED", !a[1].Bool);
                return;
            case "NAMED_SET_UNMANNED_STATUS": if (Unit(a[0]) is { } unmanned) SetUnmanned(unmanned); return;
            case "TEAM_SET_UNMANNED_STATUS":
                foreach (var obj in TeamNamed(a[0])?.AllMembers.ToList() ?? [])
                    SetUnmanned(obj);
                return;
            case "NAMED_SET_BOOBYTRAPPED": if (Unit(a[1]) is { } trapped) SetBoobytrapped(a[0].String, trapped); return;
            case "TEAM_SET_BOOBYTRAPPED":
                foreach (var obj in TeamNamed(a[1])?.AllMembers.ToList() ?? [])
                    SetBoobytrapped(a[0].String, obj);
                return;

            case "MAP_REVEAL_AT_WAYPOINT": DoRevealOrShroudAtWaypoint(a[0].String, a[1].Real, a[2].String, reveal: true); return;
            case "MAP_SHROUD_AT_WAYPOINT": DoRevealOrShroudAtWaypoint(a[0].String, a[1].Real, a[2].String, reveal: false); return;
            case "MAP_REVEAL_ALL": ForPlayerOrHumans(a[0].String, p => game.Shroud.RevealMapForPlayer(p)); return;
            case "MAP_REVEAL_ALL_PERM": ForPlayerOrHumans(a[0].String, p => game.Shroud.RevealMapForPlayerPermanently(p)); return;
            case "MAP_REVEAL_ALL_UNDO_PERM": ForPlayerOrHumans(a[0].String, p => game.Shroud.UndoRevealMapForPlayerPermanently(p)); return;
            case "MAP_SHROUD_ALL": ForPlayerOrHumans(a[0].String, p => game.Shroud.ShroudMapForPlayer(p)); return;
            case "MAP_REVEAL_PERMANENTLY_AT_WAYPOINT":
                engine.CreateNamedMapReveal(a[3].String, a[0].String, a[1].Real, a[2].String);
                engine.DoNamedMapReveal(a[3].String);
                return;
            case "MAP_UNDO_REVEAL_PERMANENTLY_AT_WAYPOINT":
                engine.UndoNamedMapReveal(a[0].String);
                engine.RemoveNamedMapReveal(a[0].String);
                return;

            case "TEAM_AVAILABLE_FOR_RECRUITMENT": TeamNamed(a[0])?.SetRecruitable(a[1].Bool); return;
            case "TEAM_COLLECT_NEARBY_FOR_TEAM": return; // (never implemented in the original)
            case "TEAM_MERGE_INTO_TEAM": DoMergeTeamIntoTeam(a[0].String, a[1].String); return;
            case "IDLE_ALL_UNITS": ForPlayerOrHumans("", p => p.SetUnitsShouldIdleOrResume(true)); return;
            case "RESUME_SUPPLY_TRUCKING": ForPlayerOrHumans("", p => p.SetUnitsShouldIdleOrResume(false)); return;

            case "TEAM_GARRISON_SPECIFIC_BUILDING": DoTeamGarrisonSpecificBuilding(a[0], a[1]); return;
            case "EXIT_SPECIFIC_BUILDING": DoExitSpecificBuilding(a[0]); return;
            case "TEAM_GARRISON_NEAREST_BUILDING": DoTeamGarrisonNearestBuilding(a[0]); return;
            case "TEAM_EXIT_ALL_BUILDINGS":
                foreach (var obj in TeamNamed(a[0])?.AllMembers.ToList() ?? [])
                    if (obj.AI is { } exitBuildingAI)
                    {
                        exitBuildingAI.SetLocomotorSet("SET_NORMAL");
                        exitBuildingAI.ExitContainer(FromScript);
                    }
                return;
            case "NAMED_GARRISON_SPECIFIC_BUILDING": DoUnitGarrisonSpecificBuilding(a[0], a[1]); return;
            case "NAMED_GARRISON_NEAREST_BUILDING": DoUnitGarrisonNearestBuilding(a[0]); return;
            case "NAMED_EXIT_BUILDING":
                if (Unit(a[0]) is { AI: { } leaveAI })
                {
                    leaveAI.SetLocomotorSet("SET_NORMAL");
                    leaveAI.ExitContainer(FromScript);
                }
                return;
            case "PLAYER_GARRISON_ALL_BUILDINGS": PlayerNamed(a[0])?.GarrisonAllUnits(FromScript); return;
            case "PLAYER_EXIT_ALL_BUILDINGS": PlayerNamed(a[0])?.UngarrisonAllUnits(FromScript); return;

            case "FREEZE_TIME": engine.DoFreezeTime(); return;
            case "SET_TREE_SWAY": engine.DoSetSway(a[0].Real, a[1].Real, a[2].Real, a[3].Int, a[4].Real); return;
            case "UNFREEZE_TIME": engine.DoUnfreezeTime(); return;
            case "SET_STOPPING_DISTANCE":
                foreach (var obj in TeamNamed(a[0])?.AllMembers.ToList() ?? [])
                {
                    if (obj.AI?.CurrentLocomotor == null)
                        break; // (the original returns at the first unit without a locomotor)
                    if (a[1].Real >= 0.5f)
                        obj.AI.SetCloseEnoughDistance(a[1].Real);
                }
                return;
            case "NAMED_SET_STOPPING_DISTANCE":
                if (Unit(a[0])?.AI is { CurrentLocomotor: not null } stopAI && a[1].Real >= 0.5f)
                    stopAI.SetCloseEnoughDistance(a[1].Real);
                return;
            case "NAMED_SET_HELD": Unit(a[0])?.SetDisabled("DISABLED_HELD", a[1].Bool); return;

            case "PLAYER_SET_MONEY":
                if (PlayerNamed(a[0]) is { } moneyPlayer)
                {
                    moneyPlayer.Money.WithdrawUpTo(moneyPlayer.Money.Amount);
                    moneyPlayer.Money.Deposit(a[1].Int);
                }
                return;
            case "PLAYER_GIVE_MONEY":
                if (PlayerNamed(a[0]) is { } givePlayer)
                {
                    if (a[1].Int < 0)
                        givePlayer.Money.WithdrawUpTo(-a[1].Int);
                    else
                        givePlayer.Money.Deposit(a[1].Int);
                }
                return;

            case "NAMED_STOP_SPECIAL_POWER_COUNTDOWN": if (PowerModule(a[0], a[1].String) is { } stopMod) stopMod.PauseCountdown(true); return;
            case "NAMED_START_SPECIAL_POWER_COUNTDOWN": if (PowerModule(a[0], a[1].String) is { } startMod) startMod.PauseCountdown(false); return;
            case "NAMED_SET_SPECIAL_POWER_COUNTDOWN":
                if (PowerModule(a[0], a[1].String) is { } setMod)
                    setMod.SetReadyFrame(game.Frame + LogicTime.FramesPerSecond * a[2].Int);
                return;
            case "NAMED_ADD_SPECIAL_POWER_COUNTDOWN":
                if (PowerModule(a[0], a[1].String) is { } addMod)
                    addMod.SetReadyFrame(addMod.ReadyFrame + LogicTime.FramesPerSecond * a[2].Int);
                return;
            case "NAMED_FIRE_SPECIAL_POWER_AT_WAYPOINT":
                if (PowerModule(a[0], a[1].String) is { } wayMod && Way(a[2].String) is { } powerWay)
                    wayMod.DoSpecialPowerAtLocation(powerWay.Position, ObjectCommandButtons.InvalidAngle, ObjectCommandButtons.CommandFiredByScript);
                return;
            case "NAMED_FIRE_SPECIAL_POWER_AT_NAMED":
                if (PowerModule(a[0], a[1].String) is { } namedMod && Unit(a[2]) is { } powerTarget)
                    namedMod.DoSpecialPowerAtObject(powerTarget, ObjectCommandButtons.CommandFiredByScript);
                return;
            case "SKIRMISH_FIRE_SPECIAL_POWER_AT_MOST_COST": DoSkirmishFireSpecialPowerAtMostCost(a[0].String, a[1].String); return;

            case "NAMED_STOP": Unit(a[0])?.AI?.Stop(FromScript); return;
            case "TEAM_STOP": DoTeamStop(a[0].String, false); return;
            case "TEAM_STOP_AND_DISBAND": DoTeamStop(a[0].String, true); return;

            case "TEAM_SET_OVERRIDE_RELATION_TO_TEAM":
                if (TeamNamed(a[0]) is { } relTeam && TeamNamed(a[1]) is { } relOther)
                    relTeam.SetOverrideTeamRelationship(relOther.Id, (Relationship)a[2].Int);
                return;
            case "TEAM_REMOVE_OVERRIDE_RELATION_TO_TEAM":
                if (TeamNamed(a[0]) is { } remTeam && TeamNamed(a[1]) is { } remOther)
                    remTeam.RemoveOverrideTeamRelationship(remOther.Id);
                return;
            case "PLAYER_SET_OVERRIDE_RELATION_TO_TEAM":
                if (game.Players.FindByNameKey(a[0].String) is { } relPlayer && TeamNamed(a[1]) is { } relPlayerTeam)
                    relPlayer.SetTeamRelationship(relPlayerTeam, (Relationship)a[2].Int);
                return;
            case "PLAYER_REMOVE_OVERRIDE_RELATION_TO_TEAM":
                if (game.Players.FindByNameKey(a[0].String) is { } remPlayer && TeamNamed(a[1]) is { } remPlayerTeam)
                    remPlayer.RemoveTeamRelationship(remPlayerTeam);
                return;
            case "TEAM_SET_OVERRIDE_RELATION_TO_PLAYER":
                if (TeamNamed(a[0]) is { } tpTeam && game.Players.FindByNameKey(a[1].String) is { } tpPlayer)
                    tpTeam.SetOverridePlayerRelationship(tpPlayer.Index, (Relationship)a[2].Int);
                return;
            case "TEAM_REMOVE_OVERRIDE_RELATION_TO_PLAYER":
                if (TeamNamed(a[0]) is { } tprTeam && game.Players.FindByNameKey(a[1].String) is { } tprPlayer)
                    tprTeam.RemoveOverridePlayerRelationship(tprPlayer.Index);
                return;
            case "TEAM_REMOVE_ALL_OVERRIDE_RELATIONS":
                if (TeamNamed(a[0]) is { } allTeam)
                {
                    allTeam.RemoveOverrideTeamRelationship(0);
                    allTeam.RemoveOverridePlayerRelationship(-1);
                }
                return;

            case "NAMED_FIRE_WEAPON_FOLLOWING_WAYPOINT_PATH": DoNamedFireWeaponFollowingWaypointPath(a[0], a[1].String); return;
            case "NAMED_USE_COMMANDBUTTON_ABILITY":
                if (Unit(a[0]) is { } cbUnit)
                    foreach (var b in cbUnit.CommandSetButtons().Where(b => b.Name == a[1].String).ToList())
                        cbUnit.DoCommandButton(b, FromScript);
                return;
            case "NAMED_USE_COMMANDBUTTON_ABILITY_ON_NAMED":
                if (Unit(a[0]) is { } cbOnUnit && Unit(a[2]) is { } cbTarget)
                    foreach (var b in cbOnUnit.CommandSetButtons().Where(b => b.Name == a[1].String).ToList())
                        cbOnUnit.DoCommandButtonAtObject(b, cbTarget, FromScript);
                return;
            case "NAMED_USE_COMMANDBUTTON_ABILITY_AT_WAYPOINT":
                if (Unit(a[0]) is { } cbAtUnit && Way(a[2].String) is { } cbWay)
                    foreach (var b in cbAtUnit.CommandSetButtons().Where(b => b.Name == a[1].String).ToList())
                        cbAtUnit.DoCommandButtonAtPosition(b, cbWay.Position, FromScript);
                return;
            case "NAMED_USE_COMMANDBUTTON_ABILITY_USING_WAYPOINT_PATH":
                if (Unit(a[0]) is { } cbPathUnit && ClosestOnPath(cbPathUnit.Position, a[2].String) is { } cbPathWay)
                    foreach (var b in cbPathUnit.CommandSetButtons().Where(b => b.Name == a[1].String).ToList())
                        cbPathUnit.DoCommandButtonUsingWaypoints(b, cbPathWay, FromScript);
                return;
            case "TEAM_USE_COMMANDBUTTON_ABILITY":
                if (TeamNamed(a[0]) is { } cbTeam && Button(a[1].String) is { } teamButton)
                    AIGroup.FromTeam(cbTeam).GroupDoCommandButton(teamButton, FromScript);
                return;
            case "TEAM_USE_COMMANDBUTTON_ABILITY_ON_NAMED":
                if (TeamNamed(a[0]) is { } cbOnTeam && Unit(a[2]) is { } cbOnTarget && Button(a[1].String) is { } onButton)
                    AIGroup.FromTeam(cbOnTeam).GroupDoCommandButtonAtObject(onButton, cbOnTarget, FromScript);
                return;
            case "TEAM_USE_COMMANDBUTTON_ABILITY_AT_WAYPOINT":
                if (TeamNamed(a[0]) is { } cbAtTeam && Way(a[2].String) is { } cbAtWay && Button(a[1].String) is { } atButton)
                    AIGroup.FromTeam(cbAtTeam).GroupDoCommandButtonAtPosition(atButton, cbAtWay.Position, FromScript);
                return;

            case "UNIT_EXECUTE_SEQUENTIAL_SCRIPT": DoUnitStartSequentialScript(a[0], a[1].String, 0); return;
            case "UNIT_EXECUTE_SEQUENTIAL_SCRIPT_LOOPING": DoUnitStartSequentialScript(a[0], a[1].String, a[2].Int - 1); return;
            case "UNIT_STOP_SEQUENTIAL_SCRIPT": if (Unit(a[0]) is { } seqStop) engine.RemoveAllSequentialScripts(seqStop); return;
            case "TEAM_EXECUTE_SEQUENTIAL_SCRIPT": DoTeamStartSequentialScript(a[0], a[1].String, 0); return;
            case "TEAM_EXECUTE_SEQUENTIAL_SCRIPT_LOOPING": DoTeamStartSequentialScript(a[0], a[1].String, a[2].Int - 1); return;
            case "TEAM_STOP_SEQUENTIAL_SCRIPT": if (TeamNamed(a[0]) is { } seqTeamStop) engine.RemoveAllSequentialScripts(seqTeamStop); return;
            case "UNIT_GUARD_FOR_FRAMECOUNT":
                if (Unit(a[0]) is { AI: { } gfAI } gfUnit)
                {
                    gfAI.SetLocomotorSet("SET_NORMAL");
                    gfAI.Guard(gfUnit.Position, FromScript);
                    engine.SetSequentialTimer(gfUnit, a[1].Int);
                }
                return;
            case "UNIT_IDLE_FOR_FRAMECOUNT":
                if (Unit(a[0]) is { AI: { } ifAI } ifUnit)
                {
                    ifAI.Stop(FromScript);
                    engine.SetSequentialTimer(ifUnit, a[1].Int);
                }
                return;
            // (The original maps the team guard action to the idle one too.)
            case "TEAM_GUARD_FOR_FRAMECOUNT":
            case "TEAM_IDLE_FOR_FRAMECOUNT":
                if (TeamNamed(a[0]) is { } idleTeam)
                {
                    AIGroup.FromTeam(idleTeam).GroupIdle(FromScript);
                    engine.SetSequentialTimer(idleTeam, a[1].Int);
                }
                return;
            case "TEAM_SPIN_FOR_FRAMECOUNT": if (TeamNamed(a[0]) is { } spinTeam) engine.SetSequentialTimer(spinTeam, a[1].Int); return;
            case "SKIRMISH_WAIT_FOR_COMMANDBUTTON_AVAILABLE_ALL":
            case "SKIRMISH_WAIT_FOR_COMMANDBUTTON_AVAILABLE_PARTIAL":
            case "TEAM_WAIT_FOR_NOT_CONTAINED_ALL":
            case "TEAM_WAIT_FOR_NOT_CONTAINED_PARTIAL":
                Log.Warning($"'{a.Name}' should never be used outside of Sequential scripts.");
                return;

            case "WATER_CHANGE_HEIGHT": game.Terrain.SetWaterHeight(a[0].String, a[1].Real); return;
            case "WATER_CHANGE_HEIGHT_OVER_TIME": game.Terrain.ChangeWaterHeightOverTime(a[0].String, a[1].Real, a[2].Real, a[3].Real); return;
            case "MAP_SWITCH_BORDER": game.Terrain.ActiveBoundary = a[0].Int; Client(a); return;
            case "OBJECT_FORCE_SELECT": DoForceObjectSelection(a); return;
            case "UNIT_DESTROY_ALL_CONTAINED":
                if (Unit(a[0])?.Contain is { Count: > 0 } destroyContain)
                    foreach (var passenger in destroyContain.Passengers.ToList())
                        passenger.Kill();
                return;
            case "TECHTREE_MODIFY_BUILDABILITY_OBJECT":
                if (game.Templates.Find(a[0].String) != null)
                    game.BuildableStatusOverrides[a[0].String] = BuildableStatusName(a[1].Int);
                return;
            case "SET_CAVE_INDEX": Unit(a[0])?.FindModule<Modules.Contain.ICaveContain>()?.TryToSetCaveIndex(a[1].Int); return;
            case "WAREHOUSE_SET_VALUE":
                if (Unit(a[0])?.FindModule<Modules.Supply.SupplyWarehouseDockUpdate>() is { } warehouse)
                    warehouse.SetCashValue(a[1].Int, game);
                return;
            case "NAMED_SET_TOPPLE_DIRECTION": engine.SetToppleDirection(a[0].String, a[1].Coord); return;
            case "UNIT_MOVE_TOWARDS_NEAREST_OBJECT_TYPE": DoMoveUnitTowardsNearest(a[0], a[1].String, a[2]); return;
            case "TEAM_MOVE_TOWARDS_NEAREST_OBJECT_TYPE": DoMoveTeamTowardsNearest(a[0], a[1].String, a[2]); return;
            case "NAMED_RECEIVE_UPGRADE":
                if (Unit(a[0]) is { } upgradeUnit && game.Catalog.Upgrade(a[1].String) != null)
                    upgradeUnit.GiveUpgrade(a[1].String);
                return;

            case "SKIRMISH_ATTACK_NEAREST_GROUP_WITH_VALUE": DoSkirmishAttackNearestGroupWithValue(a[0], (Comparison)a[1].Int, a[2].Int); return;
            case "SKIRMISH_PERFORM_COMMANDBUTTON_ON_MOST_VALUABLE_OBJECT": DoSkirmishCommandButtonOnMostValuable(a[0], a[1].String, a[2].Real, a[3].Bool); return;
            case "TEAM_ALL_USE_COMMANDBUTTON_ON_NAMED": DoTeamUseCommandButtonOnNamed(a[0], a[1].String, a[2]); return;
            case "TEAM_ALL_USE_COMMANDBUTTON_ON_NEAREST_ENEMY_UNIT": DoTeamUseCommandButtonOnNearest(a[0], a[1].String, NearestKind.Enemy, -1, ""); return;
            case "TEAM_ALL_USE_COMMANDBUTTON_ON_NEAREST_GARRISONED_BUILDING": DoTeamUseCommandButtonOnNearest(a[0], a[1].String, NearestKind.GarrisonedBuilding, -1, ""); return;
            case "TEAM_ALL_USE_COMMANDBUTTON_ON_NEAREST_KINDOF": DoTeamUseCommandButtonOnNearest(a[0], a[1].String, NearestKind.KindOf, a[2].Int, ""); return;
            case "TEAM_ALL_USE_COMMANDBUTTON_ON_NEAREST_ENEMY_BUILDING": DoTeamUseCommandButtonOnNearest(a[0], a[1].String, NearestKind.Building, -1, ""); return;
            case "TEAM_ALL_USE_COMMANDBUTTON_ON_NEAREST_ENEMY_BUILDING_CLASS": DoTeamUseCommandButtonOnNearest(a[0], a[1].String, NearestKind.BuildingClass, a[2].Int, ""); return;
            case "TEAM_ALL_USE_COMMANDBUTTON_ON_NEAREST_OBJECTTYPE": DoTeamUseCommandButtonOnNearest(a[0], a[1].String, NearestKind.ObjectType, -1, a[2].String); return;
            case "TEAM_PARTIAL_USE_COMMANDBUTTON": DoTeamPartialUseCommandButton(a[0].Real, a[1], a[2].String); return;
            case "TEAM_CAPTURE_NEAREST_UNOWNED_FACTION_UNIT": DoTeamCaptureNearestUnownedFactionUnit(a[0]); return;
            case "PLAYER_CREATE_TEAM_FROM_CAPTURED_UNITS": return; // (does nothing in the original)

            case "PLAYER_ADD_SKILLPOINTS": PlayerNamed(a[0])?.AddSkillPoints(a[1].Int); return;
            case "PLAYER_ADD_RANKLEVEL": if (PlayerNamed(a[0]) is { } rankPlayer) rankPlayer.SetRankLevel(rankPlayer.RankLevel + a[1].Int); return;
            case "PLAYER_SET_RANKLEVEL": PlayerNamed(a[0])?.SetRankLevel(a[1].Int); return;
            case "PLAYER_SET_RANKLEVELLIMIT": game.RankLevelLimit = a[0].Int; return;
            case "PLAYER_GRANT_SCIENCE":
                if (PlayerNamed(a[0]) is { } grantPlayer && game.Catalog.Science(a[1].String) != null)
                    grantPlayer.GrantScience(a[1].String);
                return;
            case "PLAYER_PURCHASE_SCIENCE":
                if (PlayerNamed(a[0]) is { } buyPlayer && game.Catalog.Science(a[1].String) != null)
                    buyPlayer.AttemptToPurchaseScience(a[1].String);
                return;
            case "PLAYER_SCIENCE_AVAILABILITY":
                if (PlayerNamed(a[0]) is { } availPlayer && Player.IsValidScienceAvailability(a[2].String) && game.Catalog.Science(a[1].String) != null)
                    availPlayer.SetScienceAvailability(a[1].String, a[2].String);
                return;
            case "PLAYER_SELECT_SKILLSET": PlayerNamed(a[0])?.SelectSkillset(a[1].Int - 1); return;
            case "PLAYER_AFFECT_RECEIVING_EXPERIENCE": if (PlayerNamed(a[0]) is { } xpPlayer) xpPlayer.SkillPointsModifier = a[1].Real; return;

            case "TEAM_SET_EMOTICON":
                if (TeamNamed(a[0]) is { } emoTeam)
                    AIGroup.FromTeam(emoTeam).GroupSetEmoticon(a[1].String, (int)(a[2].Real * LogicTime.FramesPerSecond));
                return;
            case "NAMED_SET_EMOTICON": Unit(a[0])?.SetEmoticon(a[1].String, (int)(a[2].Real * LogicTime.FramesPerSecond)); return;
            case "OBJECTLIST_ADDOBJECTTYPE": engine.DoObjectTypeListMaintenance(a[0].String, a[1].String, true); return;
            case "OBJECTLIST_REMOVEOBJECTTYPE": engine.DoObjectTypeListMaintenance(a[0].String, a[1].String, false); return;

            case "NAMED_FACE_NAMED":
                if (Unit(a[0]) is { AI: { } faceAI } && Unit(a[1]) is { } faceTarget)
                {
                    faceAI.SetLocomotorSet("SET_NORMAL");
                    faceAI.FaceObject(faceTarget, FromScript);
                }
                return;
            case "NAMED_FACE_WAYPOINT":
                if (Unit(a[0]) is { AI: { } faceWayAI } && Way(a[1].String) is { } faceWay)
                {
                    faceWayAI.SetLocomotorSet("SET_NORMAL");
                    faceWayAI.FacePosition(faceWay.Position, FromScript);
                }
                return;
            case "TEAM_FACE_NAMED":
                if (TeamNamed(a[0]) is { } faceTeam && Unit(a[1]) is { } teamFaceTarget)
                    foreach (var obj in faceTeam.AllMembers.ToList())
                        obj.AI?.FaceObject(teamFaceTarget, FromScript);
                return;
            case "TEAM_FACE_WAYPOINT":
                if (TeamNamed(a[0]) is { } faceWayTeam && Way(a[1].String) is { } teamFaceWay)
                    foreach (var obj in faceWayTeam.AllMembers.ToList())
                        obj.AI?.FacePosition(teamFaceWay.Position, FromScript);
                return;
            case "UNIT_AFFECT_OBJECT_PANEL_FLAGS": if (Unit(a[0]) is { } flagUnit) ChangeObjectPanelFlag(flagUnit, a[1].String, a[2].Bool); return;
            case "TEAM_AFFECT_OBJECT_PANEL_FLAGS":
                foreach (var obj in TeamNamed(a[0])?.AllMembers.ToList() ?? [])
                    ChangeObjectPanelFlag(obj, a[1].String, a[2].Bool);
                return;
            case "COMMANDBAR_REMOVE_BUTTON_OBJECTTYPE": DoRemoveCommandBarButton(a[0].String, a[1].String); return;
            case "COMMANDBAR_ADD_BUTTON_OBJECTTYPE_SLOT": DoAddCommandBarButton(a[0].String, a[1].String, a[2].Int); return;
            case "OBJECT_ALLOW_BONUSES":
                foreach (var obj in game.Objects)
                    obj.IsReceivingDifficultyBonus = a[0].Bool;
                engine.ObjectsShouldReceiveDifficultyBonus = a[0].Bool;
                return;
            case "DELETE_ALL_UNMANNED":
                foreach (var obj in game.Objects.ToList())
                    if (obj.IsDisabledBy("DISABLED_UNMANNED"))
                        game.DestroyObject(obj);
                return;
            case "CHOOSE_VICTIM_ALWAYS_USES_NORMAL": engine.ChooseVictimAlwaysUsesNormal = a[0].Bool; return;
            case "SET_TRAIN_HELD": Unit(a[0])?.FindModule<Modules.IRailroadBehavior>()?.SetHeld(a[1].Bool); return;
            case "SCRIPTING_OVERRIDE_HULK_LIFETIME":
                game.HulkMaxLifetimeOverride = a[0].Real < 0 ? -1 : (int)(a[0].Real * LogicTime.FramesPerSecond);
                return;
            case "OPTIONS_SET_OCCLUSION_MODE":
            case "OPTIONS_SET_DRAWICON_UI_MODE":
            case "OPTIONS_SET_PARTICLE_CAP_MODE":
                Client(a);
                return;

            // ---- Audio (logic keeps the music track for conditions) ----
            case "PLAY_SOUND_EFFECT": game.Events.Audio(new(AudioCommandKind.PlaySound, a[0].String)); return;
            case "PLAY_SOUND_EFFECT_AT":
                if (Way(a[1].String) is { } soundWay)
                    game.Events.Audio(new(AudioCommandKind.PlaySound, a[0].String, soundWay.Position));
                return;
            case "SOUND_PLAY_NAMED":
                if (Unit(a[1]) is { } soundUnit)
                    game.Events.Audio(new(AudioCommandKind.PlaySound, a[0].String, Object: soundUnit));
                return;
            case "SPEECH_PLAY": game.Events.Audio(new(AudioCommandKind.Speech, a[0].String, Flag: a[1].Bool)); return;
            case "MUSIC_SET_TRACK":
                engine.CurrentTrackName = a[0].String;
                game.Events.Audio(new(AudioCommandKind.Music, a[0].String, Flag: a[1].Bool, Flag2: a[2].Bool));
                return;
            case "MUSIC_SET_VOLUME": game.Events.Audio(new(AudioCommandKind.SetVolume, "MUSIC", Value: Math.Clamp(a[0].Real / 100f, 0, 1))); return;
            case "SOUND_SET_VOLUME": game.Events.Audio(new(AudioCommandKind.SetVolume, "SOUND", Value: Math.Clamp(a[0].Real / 100f, 0, 1))); return;
            case "SPEECH_SET_VOLUME": game.Events.Audio(new(AudioCommandKind.SetVolume, "SPEECH", Value: Math.Clamp(a[0].Real / 100f, 0, 1))); return;
            case "SOUND_DISABLE_TYPE": game.Events.Audio(new(AudioCommandKind.EnableType, a[0].String, Flag: false)); return;
            case "SOUND_ENABLE_TYPE": game.Events.Audio(new(AudioCommandKind.EnableType, a[0].String, Flag: true)); return;
            case "SOUND_ENABLE_ALL": game.Events.Audio(new(AudioCommandKind.EnableType, "", Flag: true)); return;
            case "SOUND_REMOVE_ALL_DISABLED": game.Events.Audio(new(AudioCommandKind.RemoveDisabled)); return;
            case "SOUND_REMOVE_TYPE": game.Events.Audio(new(AudioCommandKind.RemoveType, a[0].String)); return;
            case "AUDIO_OVERRIDE_VOLUME_TYPE": game.Events.Audio(new(AudioCommandKind.OverrideVolume, a[0].String, Value: a[1].Real / 100f)); return;
            case "AUDIO_RESTORE_VOLUME_TYPE": game.Events.Audio(new(AudioCommandKind.OverrideVolume, a[0].String, Value: -1)); return;
            case "AUDIO_RESTORE_VOLUME_ALL_TYPE": game.Events.Audio(new(AudioCommandKind.OverrideVolume, "", Value: -1)); return;
            case "SOUND_AMBIENT_PAUSE": game.Events.Audio(new(AudioCommandKind.PauseAmbient, Flag: true)); return;
            case "SOUND_AMBIENT_RESUME": game.Events.Audio(new(AudioCommandKind.PauseAmbient, Flag: false)); return;
            case "SUSPEND_BACKGROUND_SOUNDS": game.Events.Audio(new(AudioCommandKind.PauseSounds, Flag: true)); return;
            case "RESUME_BACKGROUND_SOUNDS": game.Events.Audio(new(AudioCommandKind.PauseSounds, Flag: false)); return;
            case "ENABLE_OBJECT_SOUND":
                if (Unit(a[0]) is { } soundOn)
                    game.Events.Audio(new(AudioCommandKind.EnableObjectSound, Object: soundOn, Flag: true));
                return;
            case "DISABLE_OBJECT_SOUND":
                if (Unit(a[0]) is { } soundOff)
                    game.Events.Audio(new(AudioCommandKind.EnableObjectSound, Object: soundOff, Flag: false));
                return;
            case "EVA_SET_ENABLED_DISABLED": game.Events.SetEvaEnabled(a[0].Bool); return;

            // ---- Things the logic notes, then the client shows ----
            case "NAMED_FLASH":
            case "NAMED_FLASH_WHITE":
                if (Unit(a[0]) is { } flashUnit && a[1].Int > 0)
                {
                    flashUnit.ScriptFlashColor = a.Name == "NAMED_FLASH_WHITE" ? 0xFFFFFF : flashUnit.IndicatorColor;
                    flashUnit.ScriptFlashCount = LogicTime.FramesPerSecond * a[1].Int / DrawableFramesPerFlash;
                }
                return;
            case "TEAM_FLASH":
            case "TEAM_FLASH_WHITE":
                if (TeamNamed(a[0]) is { } flashTeam && flashTeam.HasAnyObjects())
                    foreach (var obj in flashTeam.AllMembers.ToList())
                    {
                        obj.ScriptFlashColor = a.Name == "TEAM_FLASH_WHITE" ? 0xFFFFFF : obj.IndicatorColor;
                        obj.ScriptFlashCount = LogicTime.FramesPerSecond * a[1].Int / DrawableFramesPerFlash;
                    }
                return;
            case "NAMED_CUSTOM_COLOR": if (Unit(a[0]) is { } colorUnit) colorUnit.CustomIndicatorColor = (uint)a[1].Int & 0xFFFFFF; return;
            case "RADAR_CREATE_EVENT": game.Events.RadarEvent(RadarEventName(a[1].Int), a[0].Coord, game.Players.Local ?? game.Players.Neutral); return;
            case "OBJECT_CREATE_RADAR_EVENT":
                if (Unit(a[0]) is { } radarUnit)
                    game.Events.RadarEvent(RadarEventName(a[1].Int), radarUnit.Position, game.Players.Local ?? game.Players.Neutral);
                return;
            case "TEAM_CREATE_RADAR_EVENT":
                if (TeamNamed(a[0]) is { } radarTeam && radarTeam.HasAnyUnits() && radarTeam.EstimateTeamPosition is { } radarPos)
                    game.Events.RadarEvent(RadarEventName(a[1].Int), radarPos, game.Players.Local ?? game.Players.Neutral);
                return;
            case "SHOW_MILITARY_CAPTION": game.Events.Message(a[0].String); Client(a); return;
            case "DISPLAY_TEXT": game.Events.Message(a[0].String); return;
            case "SCREEN_SHAKE":
                Client(a);
                return;

            // ---- Camera, screen and interface: the client does these ----
            case "MOVE_CAMERA_TO":
            case "SETUP_CAMERA":
            case "ZOOM_CAMERA":
            case "PITCH_CAMERA":
            case "CAMERA_FOLLOW_NAMED":
            case "CAMERA_STOP_FOLLOW":
            case "OVERSIZE_TERRAIN":
            case "CAMERA_MOD_LOOK_TOWARD":
            case "CAMERA_MOD_FINAL_LOOK_TOWARD":
            case "MOVE_CAMERA_ALONG_WAYPOINT_PATH":
            case "ROTATE_CAMERA":
            case "CAMERA_LOOK_TOWARD_OBJECT":
            case "CAMERA_LOOK_TOWARD_WAYPOINT":
            case "RESET_CAMERA":
            case "MOVE_CAMERA_TO_SELECTION":
            case "CAMERA_MOD_FREEZE_TIME":
            case "CAMERA_MOD_FREEZE_ANGLE":
            case "CAMERA_MOD_SET_FINAL_ZOOM":
            case "CAMERA_MOD_SET_FINAL_PITCH":
            case "CAMERA_MOD_SET_FINAL_SPEED_MULTIPLIER":
            case "CAMERA_MOD_SET_ROLLING_AVERAGE":
            case "SET_VISUAL_SPEED_MULTIPLIER":
            case "SET_INFANTRY_LIGHTING_OVERRIDE":
            case "RESET_INFANTRY_LIGHTING_OVERRIDE":
            case "CAMERA_MOVE_HOME":
            case "DISPLAY_CINEMATIC_TEXT":
            case "INGAME_POPUP_MESSAGE":
            case "CAMEO_FLASH":
            case "MOVIE_PLAY_FULLSCREEN":
            case "MOVIE_PLAY_RADAR":
            case "RADAR_DISABLE":
            case "RADAR_ENABLE":
            case "RADAR_FORCE_ENABLE":
            case "RADAR_REVERT_TO_NORMAL":
            case "REFRESH_RADAR":
            case "DISABLE_INPUT":
            case "ENABLE_INPUT":
            case "DISABLE_BORDER_SHROUD":
            case "ENABLE_BORDER_SHROUD":
            case "CAMERA_LETTERBOX_BEGIN":
            case "CAMERA_LETTERBOX_END":
            case "CAMERA_BW_MODE_BEGIN":
            case "CAMERA_BW_MODE_END":
            case "DRAW_SKYBOX_BEGIN":
            case "DRAW_SKYBOX_END":
            case "SHOW_WEATHER":
            case "CAMERA_MOTION_BLUR":
            case "CAMERA_MOTION_BLUR_JUMP":
            case "CAMERA_MOTION_BLUR_FOLLOW":
            case "CAMERA_MOTION_BLUR_END_FOLLOW":
            case "CAMERA_SET_AUDIBLE_DISTANCE":
            case "SET_FPS_LIMIT":
            case "DISABLE_SPECIAL_POWER_DISPLAY":
            case "ENABLE_SPECIAL_POWER_DISPLAY":
            case "NAMED_HIDE_SPECIAL_POWER_DISPLAY":
            case "NAMED_SHOW_SPECIAL_POWER_DISPLAY":
            case "DISPLAY_COUNTDOWN_TIMER":
            case "HIDE_COUNTDOWN_TIMER":
            case "DISPLAY_COUNTER":
            case "HIDE_COUNTER":
            case "DISABLE_COUNTDOWN_TIMER_DISPLAY":
            case "ENABLE_COUNTDOWN_TIMER_DISPLAY":
            case "CAMERA_TETHER_NAMED":
            case "CAMERA_STOP_TETHER_NAMED":
            case "CAMERA_SET_DEFAULT":
            case "CAMERA_ENABLE_SLAVE_MODE":
            case "CAMERA_DISABLE_SLAVE_MODE":
            case "CAMERA_ADD_SHAKER_AT":
            case "RESIZE_VIEW_GUARDBAND":
                Client(a);
                return;
        }
        if (reported.TryAdd(a.Name, 0))
            Log.Warning($"Script action '{a.Name}' is not implemented yet.");
    }

    /// <summary>The original's DRAWABLE_FRAMES_PER_FLASH.</summary>
    const int DrawableFramesPerFlash = LogicTime.FramesPerSecond / 2;

    static readonly string[] RadarEventNames =
    [
        "INVALID", "CONSTRUCTION", "UPGRADE", "UNDER_ATTACK", "INFORMATION", "BEACON_PULSE", "INFILTRATION",
        "BATTLE_PLAN", "STEALTH_DISCOVERED", "STEALTH_NEUTRALIZED", "FAKE",
    ];

    static string RadarEventName(int type) => type >= 0 && type < RadarEventNames.Length ? RadarEventNames[type] : "INFORMATION";

    static readonly string[] BuildableStatusNames = ["Yes", "Ignore_Prerequisites", "No", "Only_By_AI"];

    static string BuildableStatusName(int status) => status >= 0 && status < BuildableStatusNames.Length ? BuildableStatusNames[status] : "Yes";

    Modules.SpecialPowers.SpecialPowerModule? PowerModule(ScriptParam unit, string power) =>
        Unit(unit) is { } obj && Power(power) is { } template ? SpecialPowerRules.ModuleFor(obj, template) : null;

    void ForPlayerOrHumans(string playerName, Action<Player> action)
    {
        if (playerName.Length > 0 && engine.GetPlayer(playerName) is { } player)
        {
            action(player);
            return;
        }
        foreach (var p in game.Players.All.ToList())
            if (p.IsHuman)
                action(p);
    }
}
