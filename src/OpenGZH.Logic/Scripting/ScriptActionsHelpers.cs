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

/// <summary>The longer script actions, ported function by function from the original.</summary>
public sealed partial class ScriptActions
{
    // ---- Ending the game ----

    /// <summary>The script won the mission: input off, the shell closes the game right away.</summary>
    void DoQuickVictory()
    {
        game.Events.ScriptClientAction(new ScriptAction { Name = "DISABLE_INPUT" });
        game.Events.ScriptClientAction(new ScriptAction { Name = "CLOSE_WINDOWS" });
        engine.EndGame(true, engine.CurrentPlayer);
        engine.StartQuickEndGameTimer();
    }

    void DoVictory()
    {
        game.Events.ScriptClientAction(new ScriptAction { Name = "DISABLE_INPUT" });
        game.Events.ScriptClientAction(new ScriptAction { Name = "VICTORY" }); // the Victorious window
        engine.EndGame(true, engine.CurrentPlayer);
        engine.StartEndGameTimer();
    }

    void DoDefeat()
    {
        game.Events.ScriptClientAction(new ScriptAction { Name = "DISABLE_INPUT" });
        game.Events.ScriptClientAction(new ScriptAction { Name = "DEFEAT" }); // the Defeat window
        engine.EndGame(false, engine.CurrentPlayer);
        engine.StartEndGameTimer();
    }

    /// <summary>The local player lost in a multiplayer game (others play on).</summary>
    void DoLocalDefeat()
    {
        engine.MarkMPLocalDefeatWindowShown();
        game.Events.ScriptClientAction(new ScriptAction { Name = "LOCALDEFEAT" });
        engine.StartCloseWindowTimer();
    }

    // ---- Moving ----

    void DoMoveToWaypoint(ScriptParam teamParam, string waypoint)
    {
        if (TeamNamed(teamParam) is not { } team || Way(waypoint) is not { } way)
            return;
        AIGroup.FromTeam(team).GroupMoveToPosition(way.Position, false, FromScript);
    }

    void DoNamedMoveToWaypoint(ScriptParam unit, string waypoint)
    {
        if (Unit(unit) is not { } obj || Way(waypoint) is not { } way || obj.AI is not { } ai)
            return;
        obj.LeaveGroup();
        ai.SetLocomotorSet("SET_NORMAL");
        ai.MoveToPositionWithMood(way.Position, FromScript);
    }

    void DoNamedFollowWaypoints(ScriptParam unit, string label, bool exact)
    {
        if (Unit(unit) is not { AI: { } ai } obj || ClosestOnPath(obj.Position, label) is not { } way)
            return;
        obj.LeaveGroup();
        ai.SetLocomotorSet("SET_NORMAL");
        ai.FollowWaypointPath(way, FromScript, exact);
    }

    void DoTeamFollowWaypoints(ScriptParam teamParam, string label, bool asTeam, bool exact)
    {
        if (TeamNamed(teamParam) is not { } team || !TeamAverage(team, out var pos) || ClosestOnPath(pos, label) is not { } way)
            return;
        var group = AIGroup.FromTeam(team);
        if (asTeam)
        {
            if (exact) group.GroupFollowWaypointPathAsTeamExact(way, FromScript);
            else group.GroupFollowWaypointPathAsTeam(way, FromScript);
        }
        else
        {
            if (exact) group.GroupFollowWaypointPathExact(way, FromScript);
            else group.GroupFollowWaypointPath(way, FromScript);
        }
    }

    /// <summary>Skirmish approach paths are labelled with the enemy's start position ("Center3").</summary>
    void DoTeamFollowSkirmishApproachPath(ScriptParam teamParam, string label, bool asTeam)
    {
        if (TeamNamed(teamParam) is not { } team || !TeamAverage(team, out var pos))
            return;
        if (engine.GetSkirmishEnemyPlayer() is not { } enemy)
            return;
        string pathLabel = label + (enemy.MpStartIndex + 1);
        if (ClosestOnPath(pos, pathLabel) is not { } way)
            return;
        if (engine.CurrentPlayer is { } aiPlayer && team.AllMembers.Count > 0)
            aiPlayer.CheckBridges(team.AllMembers[0], way);
        var group = AIGroup.FromTeam(team);
        if (asTeam)
            group.GroupFollowWaypointPathAsTeam(way, FromScript);
        else
            group.GroupFollowWaypointPath(way, FromScript);
    }

    void DoTeamMoveToSkirmishApproachPath(ScriptParam teamParam, string label)
    {
        if (TeamNamed(teamParam) is not { } team || !TeamAverage(team, out var pos))
            return;
        if (engine.GetSkirmishEnemyPlayer() is not { } enemy)
            return;
        if (ClosestOnPath(pos, label + (enemy.MpStartIndex + 1)) is not { } way)
            return;
        AIGroup.FromTeam(team).GroupMoveToPosition(way.Position, false, FromScript);
    }

    // ---- Creating things ----

    /// <summary>
    /// The original's doCreateReinforcements: the team's units appear at its reinforcement origin
    /// (in transports if it has them, loaded to fit) and go to the waypoint; transports unload and,
    /// if TransportsExit, leave again. Delivery planes drop their cargo instead.
    /// </summary>
    void DoCreateReinforcements(string teamName, string waypoint)
    {
        var proto = game.Teams.FindTeamPrototype(teamName);
        if (Way(waypoint) is not { } destWay)
            return;
        var destination = destWay.Position;
        if (proto == null)
            return;
        var info = proto.TemplateInfo;
        var origin = destination;
        bool needToMoveToDestination = false;
        if (Way(info.StartReinforceWaypoint) is { } originWay)
        {
            origin = originWay.Position;
            if (origin.X != destination.X || origin.Y != destination.Y)
                needToMoveToDestination = true;
        }
        var team = game.Teams.CreateInactiveTeam(teamName);
        var owner = team.ControllingPlayer;

        var transportTemplate = game.Templates.Find(info.TransportUnitType);
        GameObject? transport = null;
        if (transportTemplate != null)
            transport = game.CreateObject(transportTemplate, owner, origin, 0, team);
        var payload = transport?.FindModule<Modules.AI.DeliverPayloadAIUpdate>();
        var putInContainerTemplate = payload != null ? game.Templates.Find(payload.PutInContainer) : null;

        var pos = origin;
        foreach (var unitInfo in info.Units)
        {
            pos = origin;
            if (game.Templates.Find(unitInfo.UnitThingName) is not { } unitTemplate)
            {
                origin.Y = pos.Y;
                continue;
            }
            GameObject? obj = null;
            for (int j = 0; j < unitInfo.MaxUnits; j++)
            {
                obj = game.CreateObject(unitTemplate, owner, origin, 0, team);
                pos.X = origin.X + 2.25f * j * obj.Template.GeometryMajorRadius;
                pos.Z = game.Terrain.GroundHeight(pos);
                obj.SetPosition(pos);
                obj.SetOrientation(0);
            }
            if (obj != null)
                pos.Y += 2 * obj.Template.GeometryMajorRadius;
            origin.Y = pos.Y;
        }
        origin = destination;

        if (info.TeamStartsFull)
        {
            // The original's PartitionSolver packs units into the team's own transports.
            var transports = team.AllMembers.Where(o => o != transport && o.IsKindOf("TRANSPORT") && o.Contain != null).ToList();
            var units = team.AllMembers.Where(o => o != transport && !o.IsKindOf("TRANSPORT")).OrderByDescending(o => o.TransportSlotCount).ToList();
            foreach (var unit in units)
                foreach (var t in transports)
                    if (t.Contain!.IsValidContainerFor(unit, checkCapacity: true))
                    {
                        t.Contain.AddToContain(unit);
                        break;
                    }
        }

        var contain = transport?.Contain;
        if (contain != null && transport != null)
        {
            int transportCount = 1;
            foreach (var obj in team.AllMembers.ToList())
            {
                if (obj.Template.IsEquivalentTo(transport.Template) || obj.IsContained)
                    continue;
                var at = origin;
                at.X += transportCount * transport.Template.GeometryMajorRadius;
                at.Z = game.Terrain.GroundHeight(at);
                if (!contain.IsValidContainerFor(obj, false))
                    continue;
                if (!contain.IsValidContainerFor(obj, true))
                {
                    transport = game.CreateObject(transportTemplate!, owner, at, 0, team);
                    transportCount++;
                    contain = transport.Contain!;
                }
                var cargo = obj;
                if (putInContainerTemplate != null)
                {
                    var container = game.CreateObject(putInContainerTemplate, owner, at, 0, team);
                    if (container.Contain is { } inner && inner.IsValidContainerFor(obj, true))
                    {
                        inner.AddToContain(obj);
                        cargo = container;
                    }
                }
                contain.AddToContain(cargo);
            }
        }

        if (transport != null)
        {
            foreach (var obj in team.AllMembers.ToList())
            {
                var ai = obj.AI;
                if (obj.Template.IsEquivalentTo(transport.Template))
                {
                    if (obj.FindModule<Modules.AI.DeliverPayloadAIUpdate>() is { } dp)
                        dp.DeliverPayloadViaModuleData(destination);
                    else if (ai != null)
                    {
                        ai.SetLocomotorSet("SET_NORMAL");
                        ai.MoveToAndEvacuate(destination, FromScript, andExit: info.TransportsExit);
                    }
                }
                else if (!obj.IsDisabledBy("DISABLED_HELD") && ai != null)
                {
                    ai.SetLocomotorSet("SET_NORMAL");
                    ai.MoveTo(destination, FromScript);
                }
            }
        }
        else
        {
            team.SetActive();
            if (needToMoveToDestination)
                AIGroup.FromTeam(team).GroupMoveToPosition(destination, false, FromScript);
        }
    }

    /// <summary>CREATE_OBJECT / UNIT_SPAWN_NAMED_LOCATION_ORIENTATION.</summary>
    void DoCreateObject(string objectName, string thingName, string teamName, Vector3 pos, float angle)
    {
        GameObject? oldObj = null;
        if (objectName.Length > 0)
        {
            oldObj = engine.GetUnitNamed(objectName);
            if (oldObj != null && !oldObj.IsEffectivelyDead)
            {
                engine.AppendDebugMessage($"WARNING - Object with name {objectName} already exists. Failed Create.", false);
                return;
            }
        }
        var team = engine.GetTeamNamed(teamName) ?? (game.Teams.FindTeamPrototype(teamName) != null ? game.Teams.CreateTeam(teamName) : null);
        if (team == null)
        {
            engine.AppendDebugMessage("***WARNING - Team not found:*** " + teamName, true);
            return;
        }
        if (game.Templates.Find(thingName) is not { } template)
            return;
        var obj = game.CreateObject(template, team.ControllingPlayer, pos, angle, team);
        if (objectName.Length > 0)
        {
            game.SetObjectName(obj, objectName);
            if (oldObj != null || engine.DidUnitExist(objectName))
                engine.ReassignName(objectName, obj); // the name moves on to the new object
        }
        obj.SetOrientation(angle);
        obj.SetPosition(pos);
        game.TreatAsBuilt(obj);
    }

    void CreateUnitOnTeamAt(string unitName, string objType, string teamName, string waypoint)
    {
        GameObject? oldObj = unitName.Length > 0 ? engine.GetUnitNamed(unitName) : null;
        if (oldObj != null && !oldObj.IsEffectivelyDead)
        {
            engine.AppendDebugMessage($"WARNING - Object with name {unitName} already exists. Failed Create.", false);
            return;
        }
        var team = engine.GetTeamNamed(teamName) ?? (game.Teams.FindTeamPrototype(teamName) != null ? game.Teams.CreateTeam(teamName) : null);
        if (team == null)
        {
            engine.AppendDebugMessage("***WARNING - Team not found:*** " + teamName, true);
            return;
        }
        if (game.Templates.Find(objType) is not { } template)
            return;
        var at = Way(waypoint)?.Position ?? Vector3.Zero;
        var obj = game.CreateObject(template, team.ControllingPlayer, at, 0, team);
        if (unitName.Length > 0)
        {
            game.SetObjectName(obj, unitName);
            if (oldObj != null || engine.DidUnitExist(unitName))
                engine.ReassignName(unitName, obj);
        }
        game.TreatAsBuilt(obj);
    }

    void UpdateTeamAttackPrioritySet(ScriptParam teamParam, string setName)
    {
        if (TeamNamed(teamParam) is not { } team)
            return;
        var info = engine.GetAttackInfo(setName);
        if (info.Name.Length > 0)
            team.SetAttackPriorityName(info.Name);
        foreach (var obj in team.AllMembers)
            if (obj.AI is { } ai)
                ai.AttackInfo = info;
    }

    /// <summary>The team's units walk into the team's transports, packed to fit (the original's PartitionSolver).</summary>
    void DoLoadAllTransports(ScriptParam teamParam)
    {
        if (TeamNamed(teamParam) is not { } team)
            return;
        var transports = team.AllMembers.Where(o => o.IsKindOf("TRANSPORT") && o.Contain != null)
            .Select(o => (Obj: o, Space: o.Contain!.ContainMax - o.Contain.Count)).ToList();
        var units = team.AllMembers.Where(o => !o.IsKindOf("TRANSPORT")).OrderByDescending(o => o.TransportSlotCount).ToList();
        for (int u = 0; u < units.Count; u++)
        {
            var unit = units[u];
            int slots = Math.Max(1, unit.TransportSlotCount);
            for (int t = 0; t < transports.Count; t++)
            {
                if (transports[t].Space < slots || !transports[t].Obj.Contain!.IsValidContainerFor(unit, false))
                    continue;
                transports[t] = (transports[t].Obj, transports[t].Space - slots);
                if (unit.AI is { } ai)
                {
                    ai.SetLocomotorSet("SET_NORMAL");
                    ai.Enter(transports[t].Obj, FromScript);
                }
                break;
            }
        }
    }

    // ---- Hunting with abilities ----

    void DoTeamHuntWithCommandButton(ScriptParam teamParam, string ability)
    {
        if (TeamNamed(teamParam) is not { } team || Button(ability) is not { } button)
            return;
        switch (button.CommandType)
        {
            case "SPECIAL_POWER":
                if (Power(button.SpecialPower) != null && button.NeedsObjectTarget)
                    break;
                engine.AppendDebugMessage("ERROR-Team hunt with command button - cannot hunt with ability " + ability, false);
                return;
            case "SWITCH_WEAPON":
            case "FIRE_WEAPON":
            case "HIJACK_VEHICLE":
            case "CONVERT_TO_CARBOMB":
            case "SABOTAGE_BUILDING":
                break;
            default:
                engine.AppendDebugMessage("ERROR-Team hunt with command button - cannot hunt with ability " + ability, false);
                return;
        }
        foreach (var obj in team.AllMembers.ToList())
        {
            if (obj.AI == null)
                continue;
            if (!obj.CommandSetButtons().Any(b => b.Name == button.Name))
            {
                engine.AppendDebugMessage($"Error - Team hunt with command button - unit type '{obj.Template.Name}' is not valid for ability {ability}", false);
                continue;
            }
            if (obj.FindModule<Modules.AI.CommandButtonHuntUpdate>() is { } hunt)
                hunt.SetCommandButton(ability);
            else
                engine.AppendDebugMessage($"Error - Team hunt with command button - unit type '{obj.Template.Name}' requires CommandButtonHuntUpdate in .ini definition to hunt with {ability}", false);
        }
    }

    void DoTeamWander(ScriptParam teamParam, string label, bool panic)
    {
        if (TeamNamed(teamParam) is not { } team)
            return;
        foreach (var obj in team.AllMembers.ToList())
        {
            if (obj.AI is not { } ai)
                continue;
            if (ClosestOnPath(obj.Position, label) is not { } way)
                return;
            ai.SetLocomotorSet(panic ? "SET_PANIC" : "SET_WANDER");
            if (panic)
                ai.Panic(way, FromScript);
            else
                ai.Wander(way, FromScript);
        }
    }

    void SetUnmanned(GameObject obj)
    {
        obj.SetDisabled("DISABLED_UNMANNED");
        game.Events.Deselect(obj);
        obj.SetTeam(game.Players.Neutral.DefaultTeam);
    }

    /// <summary>A booby trap (StickyBombUpdate object) is stuck on a random point of the object's edge.</summary>
    void SetBoobytrapped(string thingName, GameObject obj)
    {
        if (game.Templates.Find(thingName) is not { } thing || obj.Team == null)
            return;
        var trap = game.CreateObject(thing, obj.Owner, obj.Position, 0, obj.Team);
        if (trap.FindModule<Modules.SpecialPowers.StickyBombUpdate>() is not { } update)
            return;
        var local = obj.Geometry.RandomOffsetOnPerimeter(game.Random);
        var world = obj.Position + Vector3.Transform(local, Matrix4x4.CreateRotationZ(obj.Orientation));
        update.Init(obj, null, world);
    }

    /// <summary>
    /// Reveals (or shrouds) for a moment: look then un-look at once. Without a player it is meant for
    /// the humans, but the original's mask for them is always empty, so nobody sees anything.
    /// </summary>
    void DoRevealOrShroudAtWaypoint(string waypoint, float radius, string playerName, bool reveal)
    {
        if (Way(waypoint) is not { } way)
            return;
        uint mask = playerName.Length > 0 && engine.GetPlayer(playerName) is { } player ? player.PlayerMask : 0;
        var where = new Vector2(way.Position.X, way.Position.Y);
        if (reveal)
        {
            game.Shroud.DoShroudReveal(where, radius, mask);
            game.Shroud.UndoShroudReveal(where, radius, mask);
        }
        else
        {
            game.Shroud.DoShroudCover(where, radius, mask);
            game.Shroud.UndoShroudCover(where, radius, mask);
        }
    }

    /// <summary>Everyone in the source team joins the destination team; the source team goes.</summary>
    void DoMergeTeamIntoTeam(string srcName, string destName)
    {
        var src = engine.GetTeamNamed(srcName);
        var dest = engine.GetTeamNamed(destName) ?? game.Teams.FindTeam(destName);
        if (src == null || dest == null)
            return;
        foreach (var obj in src.AllMembers.ToList())
        {
            obj.SetTeam(dest);
            UpdateTeamAndPlayerStuff(obj);
        }
        src.DeleteTeam();
        dest.SetActive();
    }

    // ---- Garrisons ----

    /// <summary>(The original's test reads "structure and nobody entered, or entered by this player" with C precedence.)</summary>
    static bool CanGarrison(GameObject building, Player player)
    {
        uint entered = building.Contain!.PlayerWhoEntered;
        return (building.IsKindOf(Kind.Structure) && entered == 0) || entered == player.PlayerMask;
    }

    void DoTeamGarrisonSpecificBuilding(ScriptParam teamParam, ScriptParam buildingParam)
    {
        if (TeamNamed(teamParam) is not { } team || Unit(buildingParam) is not { } building || building.Contain == null)
            return;
        if (!CanGarrison(building, team.ControllingPlayer))
            return;
        AIGroup.FromTeam(team).GroupEnter(building, FromScript);
    }

    void DoExitSpecificBuilding(ScriptParam buildingParam)
    {
        if (Unit(buildingParam) is not { } building || !building.IsKindOf(Kind.Structure))
            return;
        if (building.AI is { } ai)
        {
            ai.SetLocomotorSet("SET_NORMAL");
            ai.Evacuate(FromScript);
            return;
        }
        building.Contain?.RemoveAllContained();
    }

    /// <summary>The team's infantry fill the nearest buildings they may garrison (hackers go to internet centers).</summary>
    void DoTeamGarrisonNearestBuilding(ScriptParam teamParam)
    {
        if (TeamNamed(teamParam) is not { } team || team.AllMembers.Count == 0)
            return;
        var leader = team.AllMembers[0];
        var player = team.ControllingPlayer;
        IEnumerable<GameObject> buildings = game.Objects.Where(o => o.IsOffMap == leader.IsOffMap && !o.IsEffectivelyDead);
        buildings = leader.IsKindOf("MONEY_HACKER")
            ? buildings.Where(o => o.IsKindOf("FS_INTERNET_CENTER"))
            : buildings.Where(o => o.Contain is { IsGarrisonable: true } c && CanGarrison(o, player) && c.Count < c.ContainMax);
        var members = team.AllMembers.ToList();
        int next = 0;
        foreach (var building in buildings.OrderBy(o => Vector3.DistanceSquared(o.Position, leader.Position)).ToList())
        {
            if (building.Contain is not { } cmi)
                continue;
            int slotsAvailable = cmi.ContainMax - cmi.Count;
            for (int i = 0; i < slotsAvailable;)
            {
                if (next >= members.Count)
                    return;
                var obj = members[next++];
                if (obj.AI is { } ai && obj.IsKindOf(Kind.Infantry) && !obj.IsKindOf("NO_GARRISON"))
                {
                    ai.Enter(building, FromScript);
                    i++;
                }
            }
        }
    }

    void DoUnitGarrisonSpecificBuilding(ScriptParam unitParam, ScriptParam buildingParam)
    {
        if (Unit(unitParam) is not { AI: { } ai } unit || Unit(buildingParam) is not { } building || building.Contain == null)
            return;
        if (!CanGarrison(building, unit.Owner))
            return;
        ai.SetLocomotorSet("SET_NORMAL");
        ai.Enter(building, FromScript);
    }

    void DoUnitGarrisonNearestBuilding(ScriptParam unitParam)
    {
        if (Unit(unitParam) is not { AI: { } ai } unit)
            return;
        bool hacker = unit.IsKindOf("MONEY_HACKER");
        var candidates = game.Objects.Where(o => o.IsKindOf(Kind.Structure) && o.IsOffMap == unit.IsOffMap
            && (hacker ? o.IsKindOf("FS_INTERNET_CENTER") : !o.IsKindOf("FS_INTERNET_CENTER")))
            .OrderBy(o => Vector3.DistanceSquared(o.Position, unit.Position));
        foreach (var building in candidates)
        {
            if (building.Contain is not { } contain)
                continue;
            uint entered = contain.PlayerWhoEntered;
            if (!(entered == 0 || entered == unit.Owner.PlayerMask))
                continue;
            ai.SetLocomotorSet("SET_NORMAL");
            ai.Enter(building, FromScript);
            return;
        }
    }

    // ---- Special powers and abilities ----

    /// <summary>Every unit of the player with this power fires it where it hurts the current enemy most (one each).</summary>
    void DoSkirmishFireSpecialPowerAtMostCost(string playerName, string powerName)
    {
        if (engine.GetSkirmishEnemyPlayer() is not { } enemy || Power(powerName) is not { } power)
            return;
        float radius = Math.Max(50f, power.RadiusCursorRadius);
        if (engine.GetPlayer(playerName) is not { } player)
            return;
        foreach (var team in player.Teams.ToList())
            foreach (var obj in team.AllMembers.ToList())
            {
                if (SpecialPowerRules.ModuleFor(obj, power) is not { } module)
                    continue;
                if (!module.IsReady)
                    continue;
                bool found = player.ComputeSuperweaponTarget(power, out var location, enemy.Index, radius);
                if (found && power.IsType("SPECIAL_SNEAK_ATTACK") && module.ReferenceThingTemplate is { } sneak)
                    found = player.CalcClosestConstructionZoneLocation(sneak, ref location);
                if (found && location.LengthSquared() > 0)
                    module.DoSpecialPowerAtLocation(location, ObjectCommandButtons.InvalidAngle, ObjectCommandButtons.CommandFiredByScript);
                break;
            }
    }

    void DoTeamStop(string teamName, bool disband)
    {
        if (engine.GetTeamNamed(teamName) is not { } team)
            return;
        AIGroup.FromTeam(team).GroupIdle(FromScript);
        if (!disband)
            return;
        var defaultTeam = team.ControllingPlayer.DefaultTeam;
        foreach (var obj in team.AllMembers)
            if (obj.AI is { } ai)
                ai.IsRecruitable = true;
        DoMergeTeamIntoTeam(teamName, defaultTeam.Name);
    }

    /// <summary>A missile launcher fires a projectile that flies along a waypoint path (Tomahawks over the hills).</summary>
    void DoNamedFireWeaponFollowingWaypointPath(ScriptParam unitParam, string label)
    {
        if (Unit(unitParam) is not { } unit || ClosestOnPath(unit.Position, label) is not { } way)
            return;
        var weapon = unit.Weapons.All.FirstOrDefault(w => w.Template.CapableOfFollowingWaypoints);
        if (weapon == null)
            return;
        var projectile = weapon.ForceFireWeapon(unit.Position);
        if (projectile?.AI is not { } ai)
            return;
        ai.SetLocomotorSet("SET_NORMAL");
        ai.FollowWaypointPath(way, FromScript);
    }

    void DoUnitStartSequentialScript(ScriptParam unitParam, string scriptName, int loop)
    {
        if (Unit(unitParam) is not { } obj || engine.FindScript(scriptName) is not { } script)
            return;
        engine.AppendSequentialScript(new SequentialScript { ObjectId = obj.Id, Script = script, TimesToLoop = loop });
    }

    void DoTeamStartSequentialScript(ScriptParam teamParam, string scriptName, int loop)
    {
        if (TeamNamed(teamParam) is not { } team || engine.FindScript(scriptName) is not { } script)
            return;
        AIGroup.FromTeam(team).GroupIdle(FromScript);
        engine.AppendSequentialScript(new SequentialScript { Team = team, Script = script, TimesToLoop = loop });
    }

    /// <summary>OBJECT_FORCE_SELECT: the client selects the team's oldest unit of the type (and may centre on it).</summary>
    void DoForceObjectSelection(ScriptAction a)
    {
        if (TeamNamed(a[0]) is not { } team)
            return;
        var best = team.AllMembers.Where(o => o.Template.Name == a[1].String).MinBy(o => o.Id);
        if (best == null)
            return;
        game.Events.ForceSelect(best, a[2].Bool, a[3].String);
    }

    GameObject? NearestOfType(Vector3 from, string objectType, TriggerArea area, GameObject sameMapAs, Func<GameObject, bool>? extra = null)
    {
        bool Accept(GameObject o, string type) => o.Template.Name.Equals(type, StringComparison.Ordinal) && !o.IsEffectivelyDead
            && area.Contains(o.Position) && o.IsOffMap == sameMapAs.IsOffMap && (extra == null || extra(o));
        if (game.Templates.Find(objectType) != null)
            return game.Objects.Where(o => Accept(o, objectType)).MinBy(o => MathUtil.DistanceSquared2D(o.Position, from));
        GameObject? best = null;
        float bestDist = 0;
        if (engine.GetObjectTypes(objectType) is { } list)
            foreach (string type in list.Types)
            {
                var obj = game.Objects.Where(o => Accept(o, type)).MinBy(o => MathUtil.DistanceSquared2D(o.Position, from));
                if (obj == null)
                    continue;
                float dist = MathUtil.DistanceSquared2D(obj.Position, from);
                if (best == null || dist < bestDist)
                {
                    best = obj;
                    bestDist = dist;
                }
            }
        return best;
    }

    void DoMoveUnitTowardsNearest(ScriptParam unitParam, string objectType, ScriptParam areaParam)
    {
        if (Unit(unitParam) is not { AI: { } ai } unit || Area(areaParam) is not { } area)
            return;
        if (NearestOfType(unit.Position, objectType, area, unit) is not { } best)
            return;
        ai.SetLocomotorSet("SET_NORMAL");
        ai.MoveToObject(best, FromScript);
    }

    void DoMoveTeamTowardsNearest(ScriptParam teamParam, string objectType, ScriptParam areaParam)
    {
        if (TeamNamed(teamParam) is not { } team || Area(areaParam) is not { } area)
            return;
        var teamObj = team.AllMembers.FirstOrDefault(o => o.AI != null) ?? team.AllMembers.LastOrDefault();
        if (teamObj == null || team.EstimateTeamPosition is not { } teamPos)
            return;
        if (NearestOfType(teamPos, objectType, area, teamObj) is not { } best)
            return;
        foreach (var obj in team.AllMembers.ToList())
        {
            if (obj.AI is not { } ai)
                return;
            ai.SetLocomotorSet("SET_NORMAL");
            ai.MoveToObject(best, FromScript);
        }
    }

    /// <summary>
    /// The nearest 40-unit cell (the original's partition cell) whose enemy buildings and units are
    /// worth at least the value: the team attack-moves there. (The original only searches for
    /// "greater" comparisons and otherwise attack-moves to an unset point; here it then does nothing.)
    /// </summary>
    void DoSkirmishAttackNearestGroupWithValue(ScriptParam teamParam, Comparison comparison, int value)
    {
        if (TeamNamed(teamParam) is not { } team)
            return;
        var group = AIGroup.FromTeam(team);
        var player = team.ControllingPlayer;
        if (!group.GetCenter(out var groupLoc))
            return;
        if (comparison is not (Comparison.GreaterEqual or Comparison.Greater))
            return;
        const float CellSizeValue = 40f;
        var values = new Dictionary<(int, int), float>();
        foreach (var obj in game.Objects)
        {
            if (obj.IsEffectivelyDead || obj.Owner.IsNeutral || player.RelationshipTo(obj.Owner) != Relationship.Enemies)
                continue;
            var key = ((int)(obj.Position.X / CellSizeValue), (int)(obj.Position.Y / CellSizeValue));
            values[key] = values.GetValueOrDefault(key) + game.Costs.BuildCost(obj.Owner, obj.Template);
        }
        int cx = (int)(groupLoc.X / CellSizeValue), cy = (int)(groupLoc.Y / CellSizeValue);
        var best = values.Where(kv => kv.Value >= value)
            .OrderBy(kv => Math.Abs(kv.Key.Item1 - cx) + Math.Abs(kv.Key.Item2 - cy))
            .Select(kv => ((int X, int Y)?)kv.Key).FirstOrDefault();
        if (best is not { } cell)
            return;
        var loc = new Vector3(cell.X * CellSizeValue, cell.Y * CellSizeValue, 0);
        group.GroupAttackMoveToPosition(loc, -1, FromScript);
    }

    GameObject? CommandSourceFor(AIGroup group, CommandButton button) =>
        Power(button.SpecialPower) is { } power ? group.GetSpecialPowerSourceObject(power) : group.GetCommandButtonSourceObject(button.CommandType);

    bool IsEnemyOfPlayer(Player player, GameObject o, bool allowNeutral = false)
    {
        var r = player.RelationshipTo(o.Team);
        return r == Relationship.Enemies || (allowNeutral && r == Relationship.Neutral);
    }

    void DoSkirmishCommandButtonOnMostValuable(ScriptParam teamParam, string ability, float range, bool allTeamMembers)
    {
        if (TeamNamed(teamParam) is not { } team || Button(ability) is not { } button)
            return;
        var group = AIGroup.FromTeam(team);
        var player = team.ControllingPlayer;
        if (CommandSourceFor(group, button) is not { } src || !group.GetCenter(out var pos))
            return;
        var target = game.Objects
            .Where(o => IsEnemyOfPlayer(player, o) && !o.IsEffectivelyDead && o.IsOffMap == src.IsOffMap
                && MathUtil.Distance2D(o.Position, pos) <= range && button.IsValidToUseOn(src, o, null, FromScript))
            .OrderByDescending(o => game.Costs.BuildCost(o.Owner, o.Template))
            .FirstOrDefault();
        if (target != null)
            group.GroupDoCommandButtonAtObject(button, target, FromScript);
    }

    void DoTeamUseCommandButtonOnNamed(ScriptParam teamParam, string ability, ScriptParam unitParam)
    {
        if (TeamNamed(teamParam) is not { } team || Button(ability) is not { } button)
            return;
        var group = AIGroup.FromTeam(team);
        if (CommandSourceFor(group, button) is not { } src || Unit(unitParam) is not { } obj)
            return;
        if (button.IsValidToUseOn(src, obj, null, FromScript))
            group.GroupDoCommandButtonAtObject(button, obj, FromScript);
    }

    enum NearestKind { Enemy, GarrisonedBuilding, KindOf, Building, BuildingClass, ObjectType }

    /// <summary>The TEAM_ALL_USE_COMMANDBUTTON_ON_NEAREST_... family: the whole team uses the button on the nearest valid target.</summary>
    void DoTeamUseCommandButtonOnNearest(ScriptParam teamParam, string ability, NearestKind kind, int kindBit, string objectType)
    {
        if (TeamNamed(teamParam) is not { } team || Button(ability) is not { } button)
            return;
        var group = AIGroup.FromTeam(team);
        var player = team.ControllingPlayer;
        if (CommandSourceFor(group, button) is not { } src || !group.GetCenter(out var pos))
            return;
        string kindName = kindBit >= 0 && kindBit < Flags.KindOf.Count ? Flags.KindOf[kindBit] : "";
        bool Valid(GameObject o) => !o.IsEffectivelyDead && button.IsValidToUseOn(src, o, null, FromScript);
        IEnumerable<GameObject> candidates = kind switch
        {
            NearestKind.Enemy => game.Objects.Where(o => IsEnemyOfPlayer(player, o) && o.IsOffMap == src.IsOffMap && Valid(o)),
            NearestKind.GarrisonedBuilding => game.Objects.Where(o => IsEnemyOfPlayer(player, o) && o.IsKindOf(Kind.Structure)
                && o.Contain is { IsGarrisonable: true, Count: > 0 } && o.IsOffMap == src.IsOffMap && Valid(o)),
            NearestKind.KindOf => game.Objects.Where(o => IsEnemyOfPlayer(player, o) && kindName.Length > 0 && o.IsKindOf(kindName)
                && o.IsOffMap == src.IsOffMap && Valid(o)),
            NearestKind.Building => game.Objects.Where(o => IsEnemyOfPlayer(player, o) && o.IsKindOf(Kind.Structure) && o.IsOffMap == src.IsOffMap && Valid(o)),
            NearestKind.BuildingClass => game.Objects.Where(o => IsEnemyOfPlayer(player, o) && o.IsKindOf(Kind.Structure)
                && kindName.Length > 0 && o.IsKindOf(kindName) && o.IsOffMap == src.IsOffMap && Valid(o)),
            _ => ObjectTypeCandidates(objectType).Where(o => IsEnemyOfPlayer(player, o, allowNeutral: true) && o.IsOffMap == src.IsOffMap && Valid(o)),
        };
        var target = candidates.MinBy(o => MathUtil.DistanceSquared2D(o.Position, pos));
        if (target == null && kind != NearestKind.ObjectType)
            return;
        group.GroupDoCommandButtonAtObject(button, target, FromScript);
    }

    IEnumerable<GameObject> ObjectTypeCandidates(string objectType)
    {
        var names = engine.ObjectTypeNames(objectType).ToHashSet(StringComparer.Ordinal);
        return game.Objects.Where(o => names.Contains(o.Template.Name));
    }

    /// <summary>TEAM_PARTIAL_USE_COMMANDBUTTON: that percentage of the members that can use the button do.</summary>
    void DoTeamPartialUseCommandButton(float percentage, ScriptParam teamParam, string ability)
    {
        if (TeamNamed(teamParam) is not { } team || Button(ability) is not { } button)
            return;
        var list = team.AllMembers.Where(o => button.IsValidToUseOn(o, null, null, FromScript)).ToList();
        int numObjs = (int)(percentage / 100f * list.Count);
        for (int i = 0; i < list.Count && i < numObjs; i++)
            list[i].DoCommandButton(button, FromScript);
    }

    /// <summary>The team walks into the nearest unmanned vehicle that isn't an ally's.</summary>
    void DoTeamCaptureNearestUnownedFactionUnit(ScriptParam teamParam)
    {
        if (TeamNamed(teamParam) is not { } team)
            return;
        var group = AIGroup.FromTeam(team);
        var player = team.ControllingPlayer;
        if (!group.GetCenter(out var pos))
            return;
        var target = game.Objects.Where(o => IsEnemyOfPlayer(player, o, allowNeutral: true) && o.IsDisabledBy("DISABLED_UNMANNED") && !o.IsOffMap)
            .MinBy(o => MathUtil.DistanceSquared2D(o.Position, pos));
        if (target != null)
            group.GroupEnter(target, FromScript);
    }

    // ---- Object panel flags and the command bar ----

    static readonly string[] ObjectFlagNames = ["Enabled", "Powered", "Indestructible", "Unsellable", "Selectable", "AI Recruitable", "Player Targetable"];

    /// <summary>UNIT/TEAM_AFFECT_OBJECT_PANEL_FLAGS, by the original's flag names.</summary>
    static void ChangeObjectPanelFlag(GameObject obj, string flag, bool value)
    {
        if (flag == ObjectFlagNames[0])
            obj.SetScriptStatus("SCRIPT_DISABLED", !value);
        else if (flag == ObjectFlagNames[1])
            obj.SetScriptStatus("SCRIPT_UNPOWERED", !value);
        else if (flag == ObjectFlagNames[2])
        {
            if (obj.Body != null)
                obj.Body.Indestructible = value;
        }
        else if (flag == ObjectFlagNames[3])
            obj.SetScriptStatus("SCRIPT_UNSELLABLE", value);
        else if (flag == ObjectFlagNames[4])
            obj.SetSelectable(value);
        else if (flag == ObjectFlagNames[5])
        {
            if (obj.AI != null)
                obj.AI.IsRecruitable = value;
        }
        else if (flag == ObjectFlagNames[6])
            obj.SetScriptStatus("SCRIPT_TARGETABLE", value);
    }

    void DoRemoveCommandBarButton(string buttonName, string objectType)
    {
        if (game.Templates.Find(objectType) is not { } template)
            return;
        foreach (var (slot, button) in CommandSets.Buttons(game.Catalog, template.CommandSet))
            if (button.Name == buttonName)
            {
                game.ControlBarOverrides[(template.CommandSet, slot - 1)] = null;
                return;
            }
    }

    void DoAddCommandBarButton(string buttonName, string objectType, int slot)
    {
        if (game.Templates.Find(objectType) is not { } template || Button(buttonName) is not { } button)
            return;
        slot--;
        if (slot < 0 || slot >= 18)
            return;
        game.ControlBarOverrides[(template.CommandSet, slot)] = button;
    }
}
