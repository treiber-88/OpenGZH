using System.Numerics;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Locomotion;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Terrain;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// The orders mission scripts and the computer player give that a player can't: hunt, attack an
/// area or a team, follow, wander or panic along waypoint paths, guard an object or an area. Like
/// the original's hunt and attack-area states, they keep a small inner loop: find a target, attack
/// it, and look again when it is dead.
/// </summary>
public partial class AIUpdateInterface
{
    /// <summary>The AI mood (the original's attitude); only computer players' units have one.</summary>
    public Attitude Attitude { get; set; } = Attitude.Normal;

    /// <summary>The attack priorities this unit uses to pick targets (null = nearest first).</summary>
    public Scripting.AttackPriorityInfo? AttackInfo { get; set; }

    /// <summary>Whether the computer player may take this unit for another team.</summary>
    public bool IsRecruitable { get; set; } = true;

    /// <summary>The surfaces the unit's locomotors can use.</summary>
    public Surfaces ValidSurfaces => Surfaces;

    /// <summary>The last waypoint of the last path this unit finished (UNIT_COMPLETED_SEQUENTIAL... conditions).</summary>
    public Waypoint? CompletedWaypoint { get; set; }

    /// <summary>The area an attack-area or guard-area order covers.</summary>
    public TriggerArea? AreaToGuard { get; private set; }

    enum ScriptMode { None, Hunt, AttackArea, AttackTeam, FollowPath, Wander, WanderInPlace, Panic, GuardObject }

    ScriptMode scriptMode;
    bool issuingScriptOrder;
    int nextEnemyScanFrame;
    Team? attackTeam;
    Waypoint? currentWaypoint, priorWaypoint;
    bool followAsAttackMove;
    Vector3 wanderOrigin;
    Vector2 wanderOffset;
    uint guardObjectId;

    /// <summary>Enemy scans of the hunt and attack-area states (the original's ENEMY_SCAN_RATE, once a second).</summary>
    const int EnemyScanRate = LogicTime.FramesPerSecond;

    /// <summary>Called by every order: a new order from outside ends the script mode.</summary>
    void NoteOrder()
    {
        if (State == AIState.Docking)
            HaltDock();
        if (!issuingScriptOrder)
        {
            scriptMode = ScriptMode.None;
            attackTeam = null;
        }
    }

    void Internal(Action order)
    {
        bool saved = issuingScriptOrder;
        issuingScriptOrder = true;
        try { order(); }
        finally { issuingScriptOrder = saved; }
    }

    bool IsAIControlled => !Owner.Owner.IsHuman;

    /// <summary>Angry mob members ignore moods (the original tests INFANTRY + IGNORED_IN_GUI).</summary>
    bool IgnoresMood => Owner.IsKindOf(Kind.Infantry) && Owner.IsKindOf("IGNORED_IN_GUI");

    /// <summary>The mood matrix for moves: an alert or aggressive computer unit attack-moves instead.</summary>
    public bool MoveBecomesAttackMove => IsAIControlled && !IgnoresMood && Attitude is Attitude.Alert or Attitude.Aggressive;

    /// <summary>The mood matrix for moves: a sleeping computer unit stays put.</summary>
    public bool MoveBecomesIdle => IsAIControlled && !IgnoresMood && Attitude == Attitude.Sleep;

    /// <summary>Idle auto-targeting range scale from the mood (sleeping and passive units don't look at all).</summary>
    float MoodRangeScale()
    {
        if (!IsAIControlled || IgnoresMood)
            return 1;
        return Attitude switch
        {
            Attitude.Sleep or Attitude.Passive => 0,
            Attitude.Alert => Game.AIData.AlertRangeModifier,
            Attitude.Aggressive => Game.AIData.AggressiveRangeModifier,
            _ => 1,
        };
    }

    /// <summary>A move with the mood matrix applied (script and computer moves).</summary>
    public void MoveToPositionWithMood(Vector3 destination, CommandSource source)
    {
        if (MoveBecomesIdle)
        {
            Stop(source);
            return;
        }
        if (MoveBecomesAttackMove)
            AttackMoveTo(destination, source);
        else
            MoveTo(destination, source);
    }

    // ---- Hunt (the original's AIHuntState) ----

    public void Hunt(CommandSource source)
    {
        Stop(source);
        scriptMode = ScriptMode.Hunt;
        LastCommandSource = source;
        nextEnemyScanFrame = Frame + Game.Random.Int(0, EnemyScanRate);
        Wake();
    }

    /// <summary>The script's AttackArea: attack enemies inside a trigger area until there are none.</summary>
    public void AttackArea(TriggerArea area, CommandSource source)
    {
        Stop(source);
        AreaToGuard = area;
        scriptMode = ScriptMode.AttackArea;
        LastCommandSource = source;
        nextEnemyScanFrame = Frame + Game.Random.Int(0, EnemyScanRate);
        Wake();
    }

    /// <summary>Attack the members of a team, nearest first, until none are left.</summary>
    public void AttackTeam(Team team, CommandSource source, int maxShots = -1)
    {
        Stop(source);
        attackTeam = team;
        scriptMode = ScriptMode.AttackTeam;
        LastCommandSource = source;
        nextEnemyScanFrame = Frame;
        Wake();
    }

    /// <summary>Is the unit in a hunt or attack-area loop (it counts as busy, not idle)?</summary>
    public bool IsInScriptMode => scriptMode != ScriptMode.None;

    bool UpdateScriptModeWhileIdle()
    {
        switch (scriptMode)
        {
            case ScriptMode.Hunt:
            case ScriptMode.AttackArea:
            case ScriptMode.AttackTeam:
                ScanForScriptVictim();
                return true;
            case ScriptMode.FollowPath:
            case ScriptMode.Wander:
            case ScriptMode.Panic:
                // An attack on the way finished: carry on to the current waypoint.
                if (currentWaypoint != null)
                    MoveToCurrentWaypoint();
                else
                    scriptMode = ScriptMode.None;
                return true;
            case ScriptMode.WanderInPlace:
                PickWanderInPlaceGoal();
                return true;
        }
        return false;
    }

    void ScanForScriptVictim()
    {
        if (Frame < nextEnemyScanFrame)
        {
            Decelerate();
            return;
        }
        nextEnemyScanFrame = Frame + EnemyScanRate;
        if (Owner.Weapons.IsOutOfAmmo && !Owner.IsKindOf(Kind.Projectile))
        {
            scriptMode = ScriptMode.None;
            return;
        }
        GameObject? victim = null;
        switch (scriptMode)
        {
            case ScriptMode.Hunt:
                victim = FindHuntVictim();
                break;
            case ScriptMode.AttackArea:
                if (AreaToGuard is not { } area)
                {
                    scriptMode = ScriptMode.None;
                    return;
                }
                victim = Logic.AI.Targeting.FindClosestEnemy(Owner, 9999.9f, AttackInfo, o => area.Contains(o.Position));
                break;
            case ScriptMode.AttackTeam:
                victim = attackTeam?.Members.Where(o => Logic.AI.Targeting.IsPossibleToAttack(Owner, o) && !Logic.AI.Targeting.IsStealthedAndUndetectedFor(Owner, o))
                    .MinBy(o => MathUtil.DistanceSquared2D(o.Position, Owner.Position));
                break;
        }
        if (victim != null)
        {
            Internal(() => AttackObject(victim, CommandSource.AI));
            return;
        }
        if (scriptMode == ScriptMode.Hunt && Owner.Owner.UnitsShouldHunt)
            return; // keep looking
        scriptMode = ScriptMode.None;
        attackTeam = null;
    }

    GameObject? FindHuntVictim()
    {
        var info = AttackInfo;
        var team = Owner.Team;
        GameObject? teamVictim = team != null && team.Prototype.TemplateInfo.AttackCommonTarget ? team.TeamTargetObject : null;
        if (teamVictim != null && info == null)
            return teamVictim;
        var victim = Logic.AI.Targeting.FindClosestEnemy(Owner, 9999.9f, info);
        if (victim == null && Owner.Owner.UnitsShouldHunt)
            victim = Logic.AI.Targeting.FindClosestEnemy(Owner, 9999.9f, null);
        if (team != null && team.Prototype.TemplateInfo.AttackCommonTarget)
        {
            if (teamVictim != null && info != null)
            {
                victim ??= teamVictim;
                if (info.GetPriority(teamVictim.Template) >= info.GetPriority(victim.Template))
                    victim = teamVictim;
            }
            team.SetTeamTargetObject(victim);
        }
        return victim;
    }

    // ---- Waypoint paths (the original's AIFollowWaypointPathState and its wander and panic variants) ----

    /// <summary>Follow a waypoint path from this waypoint, choosing branches at random and never doubling back.</summary>
    public void FollowWaypointPath(Waypoint start, CommandSource source, bool exact = false)
    {
        NoteOrder();
        if (MoveBecomesIdle)
        {
            Stop(source);
            return;
        }
        StartWaypointMode(ScriptMode.FollowPath, start, source, MoveBecomesAttackMove);
    }

    /// <summary>Follow a waypoint path attacking anything met on the way.</summary>
    public void AttackFollowWaypointPath(Waypoint start, CommandSource source)
    {
        NoteOrder();
        StartWaypointMode(ScriptMode.FollowPath, start, source, attackMove: true);
    }

    public void Wander(Waypoint start, CommandSource source)
    {
        NoteOrder();
        StartWaypointMode(ScriptMode.Wander, start, source, attackMove: false);
    }

    public void Panic(Waypoint start, CommandSource source)
    {
        NoteOrder();
        Owner.SetCondition("PANICKING");
        StartWaypointMode(ScriptMode.Panic, start, source, attackMove: false);
    }

    void StartWaypointMode(ScriptMode mode, Waypoint start, CommandSource source, bool attackMove)
    {
        Internal(() => Stop(source));
        scriptMode = mode;
        LastCommandSource = source;
        priorWaypoint = null;
        currentWaypoint = start;
        followAsAttackMove = attackMove;
        PickWanderOffset();
        MoveToCurrentWaypoint();
    }

    void PickWanderOffset()
    {
        wanderOffset = Vector2.Zero;
        if (scriptMode is not (ScriptMode.Wander or ScriptMode.Panic) || CurrentLocomotor == null || CurrentLocomotor.Template.WanderWidthFactor <= 0)
            return;
        int delta = Math.Max(1, (int)MathF.Floor(CurrentLocomotor.Template.WanderWidthFactor + 0.5f));
        wanderOffset = new Vector2(Game.Random.Int(-delta, delta), Game.Random.Int(-delta, delta)) * Formats.Maps.MapFile.CellSize;
    }

    void MoveToCurrentWaypoint()
    {
        if (currentWaypoint == null)
            return;
        var goal = currentWaypoint.Position + new Vector3(wanderOffset, 0);
        Internal(() =>
        {
            if (followAsAttackMove)
                AttackMoveTo(goal, LastCommandSource);
            else
                MoveTo(goal, LastCommandSource);
            if (!followAsAttackMove)
                State = AIState.FollowingPath;
        });
    }

    /// <summary>The original's hasNextWaypoint / getNextWaypoint: a random link, but not straight back.</summary>
    Waypoint? NextWaypoint()
    {
        var current = currentWaypoint!;
        int links = current.Next.Count;
        bool hasNext = links > 0 && (priorWaypoint == null || links > 1 || priorWaypoint != current.Next[0]);
        if (!hasNext)
        {
            priorWaypoint = current;
            return null;
        }
        int skip = priorWaypoint != null ? current.Next.IndexOf(priorWaypoint) : -1;
        int which;
        if (skip >= 0)
        {
            which = Game.Random.Int(0, links - 2);
            if (which == skip)
                which = links - 1;
        }
        else
            which = Game.Random.Int(0, links - 1);
        priorWaypoint = current;
        return current.Next[which];
    }

    /// <summary>Arrived somewhere: a waypoint mode moves on to the next waypoint. Returns true if it did.</summary>
    bool OnScriptArrived()
    {
        switch (scriptMode)
        {
            case ScriptMode.FollowPath:
            case ScriptMode.Wander:
            case ScriptMode.Panic:
                if (currentWaypoint == null)
                    return false;
                currentWaypoint = NextWaypoint();
                if (currentWaypoint == null)
                {
                    CompletedWaypoint = priorWaypoint;
                    if (scriptMode == ScriptMode.Panic)
                        Owner.SetCondition("PANICKING", false);
                    scriptMode = ScriptMode.None;
                    return false;
                }
                PickWanderOffset();
                MoveToCurrentWaypoint();
                return true;
            case ScriptMode.WanderInPlace:
                PickWanderInPlaceGoal();
                return true;
        }
        return false;
    }

    // ---- Wander in place (the original's AIWanderInPlaceState) ----

    public void WanderInPlace(CommandSource source)
    {
        Internal(() => Stop(source));
        scriptMode = ScriptMode.WanderInPlace;
        LastCommandSource = source;
        wanderOrigin = Owner.Position;
        SetLocomotorSet("SET_WANDER");
        PickWanderInPlaceGoal();
    }

    void PickWanderInPlaceGoal()
    {
        int delta = 3;
        if (CurrentLocomotor != null)
            delta = (int)MathF.Floor(CurrentLocomotor.Template.WanderAboutPointRadius / Formats.Maps.MapFile.CellSize + 0.5f);
        var offset = new Vector3(Game.Random.Int(-delta, delta), Game.Random.Int(-delta, delta), 0) * Formats.Maps.MapFile.CellSize;
        var goal = wanderOrigin + offset;
        Internal(() => MoveTo(goal, LastCommandSource));
    }

    // ---- Guarding ----

    /// <summary>Guard an object: stay near it and fight whatever comes close.</summary>
    public void GuardObject(GameObject target, CommandSource source)
    {
        Guard(target.Position, source);
        scriptMode = ScriptMode.GuardObject;
        guardObjectId = target.Id;
    }

    /// <summary>Guard a trigger area: around its middle, out to its edge.</summary>
    public void GuardArea(TriggerArea area, CommandSource source)
    {
        var center = new Vector3(area.Center, 0);
        center.Z = Game.Terrain.GroundHeight(center);
        Guard(center, source);
        AreaToGuard = area;
        guardRadius = Math.Max(area.Radius, 150f);
    }

    /// <summary>Tunnel network guards wait inside the network and come out to fight (the original's AITNGuard).</summary>
    public void GuardTunnelNetwork(CommandSource source) => Guard(Owner.Position, source);

    /// <summary>Guarding an object keeps the guard point on the object.</summary>
    void UpdateGuardedObject()
    {
        if (scriptMode != ScriptMode.GuardObject)
            return;
        var target = Game.FindObject(guardObjectId);
        if (target == null || target.IsEffectivelyDead)
        {
            scriptMode = ScriptMode.None;
            return;
        }
        guardPosition = target.Position;
    }

    /// <summary>The locomotor's arrival distance (SET_STOPPING_DISTANCE scripts).</summary>
    public void SetCloseEnoughDistance(float distance)
    {
        if (CurrentLocomotor != null)
            CurrentLocomotor.CloseEnoughDistance = distance;
    }

    /// <summary>
    /// Sets the HORDE / NATIONALISM / FANATICISM weapon bonuses from the horde modules and the
    /// player's upgrades (the original's evaluateMoraleBonus).
    /// </summary>
    public void EvaluateMoraleBonus()
    {
        var us = Owner;
        var player = us.Owner;
        bool horde = false;
        bool nationalism = player.HasUpgrade("Upgrade_Nationalism");
        bool fanaticism = player.HasUpgrade("Upgrade_Fanaticism");
        foreach (var hui in us.FindModules<Updates.HordeUpdate>())
        {
            if (!hui.IsInHorde)
                continue;
            horde = true;
            if (!hui.IsAllowedNationalism)
            {
                nationalism = false;
                fanaticism = false;
            }
        }
        us.SetWeaponBonus("HORDE", horde);
        if (nationalism)
        {
            us.SetWeaponBonus("NATIONALISM");
            us.SetWeaponBonus("FANATICISM", fanaticism);
        }
        else
            us.SetWeaponBonus("NATIONALISM", false);
    }
}
