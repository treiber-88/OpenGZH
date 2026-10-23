using System.Numerics;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.AI;

/// <summary>
/// A set of units given one order together (the original's AIGroup). Scripts and the computer
/// player make one from a team for each order; it is not kept afterwards.
/// </summary>
public sealed class AIGroup
{
    readonly List<GameObject> members = new();

    public IReadOnlyList<GameObject> Members => members;
    public int Count => members.Count;
    public bool IsEmpty => members.Count == 0;

    public static AIGroup FromTeam(Team team)
    {
        var group = new AIGroup();
        foreach (var obj in team.AllMembers.ToList())
            group.Add(obj);
        return group;
    }

    /// <summary>Only things with an AI, buildings and ALWAYS_SELECTABLE things can be in a group.</summary>
    public void Add(GameObject obj)
    {
        if (obj.AI == null && !obj.IsKindOf(Kind.Structure) && !obj.IsKindOf("ALWAYS_SELECTABLE"))
            return;
        if (!members.Contains(obj))
            members.Add(obj);
    }

    /// <summary>The middle of the group's mobile members (held units don't count); false when there is nobody.</summary>
    public bool GetCenter(out Vector3 center)
    {
        center = Vector3.Zero;
        int count = 0;
        foreach (var obj in members)
            if (!obj.IsDisabledBy("DISABLED_HELD") && obj.AI != null)
            {
                center += obj.Position;
                count++;
            }
        if (count == 0)
            foreach (var obj in members)
                if (!obj.IsDisabledBy("DISABLED_HELD"))
                {
                    center += obj.Position;
                    count++;
                }
        if (count > 0)
            center /= count;
        return count > 0;
    }

    public Vector3 Center
    {
        get
        {
            GetCenter(out var center);
            return center;
        }
    }

    /// <summary>The slowest member's speed (the group moves together at it).</summary>
    public float Speed => members.Where(o => o.AI?.CurrentLocomotor != null).Select(o => o.AI!.CurrentLocomotor!.MaxSpeedFor(o)).DefaultIfEmpty(0).Min();

    IEnumerable<Modules.AI.AIUpdateInterface> AIs => members.ToList().Select(o => o.AI).Where(a => a != null)!;

    public bool IsIdle => members.All(o => o.AI == null || o.AI.IsIdle || o.IsEffectivelyDead);

    /// <summary>Every member moves to (around) the point, spread out so they don't pile up.</summary>
    public void GroupMoveToPosition(Vector3 position, bool addWaypoint, CommandSource source)
    {
        var mobile = members.Where(o => o.AI != null && !o.IsKindOf(Kind.Structure)).ToList();
        var offsets = Formation.Offsets(mobile, position);
        for (int i = 0; i < mobile.Count; i++)
        {
            var ai = mobile[i].AI!;
            var destination = position + (i < offsets.Count ? offsets[i] : Vector3.Zero);
            if (source == CommandSource.Player)
                ai.MoveTo(destination, source);
            else
                ai.MoveToPositionWithMood(destination, source);
        }
    }

    public void GroupAttackMoveToPosition(Vector3 position, int maxShots, CommandSource source)
    {
        foreach (var obj in members.ToList())
            if (obj.AI is { } ai)
            {
                if (obj.Weapons.HasAnyWeapon)
                    ai.AttackMoveTo(position, source);
                else
                    ai.MoveTo(position, source);
            }
    }

    public void GroupFollowWaypointPath(Waypoint way, CommandSource source)
    {
        foreach (var ai in AIs)
            ai.FollowWaypointPath(way, source);
    }

    public void GroupFollowWaypointPathExact(Waypoint way, CommandSource source)
    {
        foreach (var ai in AIs)
            ai.FollowWaypointPath(way, source, exact: true);
    }

    /// <summary>As a team: members follow together (each starts from the same waypoint).</summary>
    public void GroupFollowWaypointPathAsTeam(Waypoint way, CommandSource source)
    {
        foreach (var obj in members)
            if (obj.Team != null)
                obj.Team.CurrentWaypoint = way;
        GroupFollowWaypointPath(way, source);
    }

    public void GroupFollowWaypointPathAsTeamExact(Waypoint way, CommandSource source) => GroupFollowWaypointPathAsTeam(way, source);

    public void GroupIdle(CommandSource source)
    {
        foreach (var obj in members.ToList())
        {
            if (obj.AI is { } ai)
                ai.Stop(source);
            else if (obj.Contain != null)
                foreach (var passenger in obj.Contain.Passengers.ToList())
                    passenger.AI?.Stop(source);
        }
    }

    void AttackObjectPrivate(GameObject? victim, int maxShots, CommandSource source)
    {
        if (victim == null)
            return;
        var ordered = members.Where(o => !o.IsDisabledBy("DISABLED_HELD"))
            .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, victim.Position)).ToList();
        foreach (var unit in ordered)
        {
            if (unit.Contain is { PassengersAllowedToFire: true } contain)
                foreach (var passenger in contain.Passengers.ToList())
                    if (Targeting.IsPossibleToAttack(passenger, victim))
                        passenger.AI?.AttackObject(victim, source, maxShots);
            if (unit.AI is { } ai && unit != victim)
                ai.AttackObject(victim, source, maxShots);
        }
    }

    public void GroupAttackObject(GameObject victim, int maxShots, CommandSource source) => AttackObjectPrivate(victim, maxShots, source);
    public void GroupForceAttackObject(GameObject victim, int maxShots, CommandSource source) => AttackObjectPrivate(victim, maxShots, source);

    public void GroupAttackTeam(Team team, int maxShots, CommandSource source)
    {
        foreach (var ai in AIs)
            ai.AttackTeam(team, source, maxShots);
    }

    public void GroupAttackPosition(Vector3? position, int maxShots, CommandSource source)
    {
        foreach (var obj in members.ToList())
        {
            var at = position ?? obj.Position;
            if (obj.Contain is { PassengersAllowedToFire: true } contain)
                foreach (var passenger in contain.Passengers.ToList())
                    passenger.AI?.AttackPosition(at, source, maxShots);
            obj.AI?.AttackPosition(at, source, maxShots);
        }
    }

    public void GroupHunt(CommandSource source)
    {
        foreach (var ai in AIs)
            ai.Hunt(source);
    }

    public void GroupEnter(GameObject container, CommandSource source)
    {
        foreach (var ai in AIs)
            ai.Enter(container, source);
    }

    public void GroupExit(CommandSource source)
    {
        foreach (var ai in AIs)
            ai.ExitContainer(source);
    }

    /// <summary>Everyone out: flying transports land first, buildings just open up.</summary>
    public void GroupEvacuate(CommandSource source)
    {
        foreach (var obj in members.ToList())
        {
            if (obj.AI is { } ai)
            {
                if (obj.IsKindOf(Kind.Aircraft) && obj.IsAirborneTarget)
                {
                    var pos = obj.Position with { Z = obj.Game.Terrain.LayerHeight(obj.Position.X, obj.Position.Y, obj.Layer) };
                    ai.MoveToAndEvacuate(pos, source);
                }
                else
                    ai.Evacuate(source);
            }
            else if (obj.IsKindOf(Kind.Structure))
                obj.Contain?.OrderAllPassengersToExit(source, instantly: false);
        }
    }

    public void GroupMoveToAndEvacuate(Vector3 position, CommandSource source)
    {
        foreach (var ai in AIs)
            ai.MoveToAndEvacuate(position, source);
    }

    public void GroupMoveToAndEvacuateAndExit(Vector3 position, CommandSource source)
    {
        foreach (var ai in AIs)
            ai.MoveToAndEvacuate(position, source, andExit: true);
    }

    public void GroupGuardPosition(Vector3 position, CommandSource source)
    {
        foreach (var ai in AIs)
            ai.Guard(position, source);
    }

    public void GroupGuardObject(GameObject target, CommandSource source)
    {
        foreach (var ai in AIs)
            ai.GuardObject(target, source);
    }

    public void GroupGuardArea(TriggerArea area, CommandSource source)
    {
        foreach (var ai in AIs)
            ai.GuardArea(area, source);
    }

    public void GroupAttackArea(TriggerArea area, CommandSource source)
    {
        foreach (var ai in AIs)
            ai.AttackArea(area, source);
    }

    public void SetAttitude(Attitude attitude)
    {
        foreach (var ai in AIs)
            ai.Attitude = attitude;
    }

    public void GroupDoCommandButton(CommandButton button, CommandSource source)
    {
        foreach (var obj in members.ToList())
            obj.DoCommandButton(button, source);
    }

    public void GroupDoCommandButtonAtPosition(CommandButton button, Vector3 position, CommandSource source)
    {
        foreach (var obj in members.ToList())
            obj.DoCommandButtonAtPosition(button, position, source);
    }

    public void GroupDoCommandButtonAtObject(CommandButton button, GameObject? target, CommandSource source)
    {
        foreach (var obj in members.ToList())
            obj.DoCommandButtonAtObject(button, target, source);
    }

    public void GroupDoCommandButtonUsingWaypoints(CommandButton button, Waypoint way, CommandSource source)
    {
        foreach (var obj in members.ToList())
            obj.DoCommandButtonUsingWaypoints(button, way, source);
    }

    /// <summary>The first member with a module for this power.</summary>
    public GameObject? GetSpecialPowerSourceObject(SpecialPowerTemplate power) =>
        members.FirstOrDefault(o => Modules.SpecialPowers.SpecialPowerRules.ModuleFor(o, power) != null);

    /// <summary>The first member whose command set has a button of this command type.</summary>
    public GameObject? GetCommandButtonSourceObject(string commandType) =>
        members.FirstOrDefault(o => o.CommandSetButtons().Any(b => b.CommandType == commandType));

    public void GroupSetEmoticon(string name, int frames)
    {
        foreach (var obj in members)
            obj.SetEmoticon(name, frames);
    }
}
