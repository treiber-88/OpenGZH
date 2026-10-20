using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Locomotion;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>What a unit's AI is currently doing.</summary>
public enum AIState { Idle, Moving, AttackingObject, AttackingPosition, AttackMoving, Guarding, FollowingPath, Busy, Facing }

/// <summary>
/// The standard unit brain ("AIUpdateInterface"): moves along paths using the object's locomotor,
/// attacks targets with its weapons and turrets, guards areas, and automatically engages enemies
/// when idle. Specialised AIs (dozers, harvesters, aircraft, ...) derive from this class.
/// </summary>
public class AIUpdateInterface : Module, IUpdateModule, ICreateModule
{
    public AIState State { get; protected set; } = AIState.Idle;

    // AIs steer before physics moves anything, and keep working inside transports (DISABLED_HELD).
    public UpdatePhase Phase => UpdatePhase.Initial;
    public FlagSet DisabledTypesToProcess => DisabledMask.Held;
    public CommandSource LastCommandSource { get; private set; } = CommandSource.AI;

    public bool IsIdle => State == AIState.Idle;
    public bool IsBusy => State == AIState.Busy;

    /// <summary>Marks the unit as busy with something a module controls (cleaning up, an ability), so it doesn't auto-acquire.</summary>
    public void SetBusy(CommandSource source)
    {
        LastCommandSource = source;
        ClearPath();
        State = AIState.Busy;
    }
    /// <summary>The original's isMoving: not idle, and either steering somewhere or still sliding.</summary>
    public bool IsMoving => !IsIdle && (goalType != LocomotorGoal.None || (Physics?.VelocityMagnitude ?? 0) > 0.001f);

    /// <summary>Turrets defined in the module (most units have at most one).</summary>
    public List<Turret> Turrets { get; } = new();

    /// <summary>The locomotor in use (chosen from the current set for the surface the unit is on).</summary>
    public Locomotor? CurrentLocomotor { get; private set; }
    public LocomotorTemplate? Locomotor => CurrentLocomotor?.Template;
    public string LocomotorSetName { get; private set; } = "";

    public uint TargetId { get; private set; }
    public Vector3 TargetPosition { get; private set; }
    public Vector3 Destination { get; private set; }
    public IReadOnlyList<Vector3> Path => path?.Nodes ?? [];

    /// <summary>The unit's current speed (world units per frame).</summary>
    public float CurrentSpeed => Physics?.VelocityMagnitude ?? 0;

    /// <summary>What the locomotor is steering towards this frame (the original's locomotor goal).</summary>
    enum LocomotorGoal { None, PositionOnPath, PositionExplicit, Angle }

    const float FastAsPossible = 999999f;

    Path? path;
    readonly Dictionary<string, List<LocomotorTemplate>> locomotorSets = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Locomotor> currentSet = new();
    LocomotorGoal goalType;
    Vector3 goalData;
    float desiredSpeed = FastAsPossible;
    Vector3 finalPosition;
    bool doFinalPosition;
    int nextRepathFrame;

    // Blocking by other units (filled in by ProcessCollision during the collision pass).
    bool isBlocked, isBlockedAndStuck;
    int blockedFrames;
    float curMaxBlockedSpeed = FastAsPossible, bumpSpeedLimit = FastAsPossible;
    int ignoreCollisionsUntil;
    uint moveOutOfWayId;

    /// <summary>Collisions with this object don't count (e.g. the building a unit is walking into).</summary>
    public uint IgnoredObstacleId { get; private set; }
    public void IgnoreObstacle(GameObject? obj) => IgnoredObstacleId = obj?.Id ?? 0;
    public void SetIgnoreCollisionTime(int frames) => ignoreCollisionsUntil = Frame + frames;

    PhysicsBehavior? physicsCache;
    PhysicsBehavior? Physics => physicsCache ??= Owner.FindModule<PhysicsBehavior>();
    int nextAutoAcquireFrame;
    int moodCheckFrames;
    bool autoAcquireWhenIdle;
    bool autoAcquireWhenStealthed;
    Vector3 guardPosition;
    float guardRadius = 150f;
    List<Vector3>? waypointRoute;
    int waypointRouteIndex;

    protected override void Initialize()
    {
        foreach (var turretKey in new[] { "Turret", "AltTurret" })
            if (Definition.Settings.TryGet(turretKey, out var turret))
                Turrets.Add(new Turret(turret.IsSequence ? turret.Items[0] : turret));

        autoAcquireWhenIdle = Setting("AutoAcquireEnemiesWhenIdle").Split(' ').Any(w => w.Equals("Yes", StringComparison.OrdinalIgnoreCase));
        autoAcquireWhenStealthed = Setting("AutoAcquireEnemiesWhenIdle").Contains("STEALTHED", StringComparison.OrdinalIgnoreCase);
        moodCheckFrames = Math.Max(1, FramesSetting("MoodAttackCheckRate", 250));
    }

    public virtual void OnCreate()
    {
        // Locomotors are listed on the object: "Locomotor: SET_NORMAL CrusaderLocomotor" (possibly several lines).
        if (Owner.Template.Rules.TryGet("Locomotor", out var entries))
        {
            foreach (var entry in entries.AsList())
            {
                string[] words = RuleValue.Words(entry.Value);
                if (words.Length < 2)
                    continue;
                var list = words.Skip(1).Select(Game.Catalog.Locomotor).Where(l => l != null).Cast<LocomotorTemplate>().ToList();
                if (list.Count > 0)
                    locomotorSets[words[0]] = list;
            }
        }
        SetLocomotorSet("SET_NORMAL");
        guardPosition = Owner.Position;
    }

    /// <summary>Set by LocomotorSetUpgrade: the normal set becomes SET_NORMAL_UPGRADED (if the object has one).</summary>
    public bool LocomotorUpgraded { get; private set; }

    public void UpgradeLocomotors()
    {
        LocomotorUpgraded = true;
        if (LocomotorSetName is "SET_NORMAL" or "SET_NORMAL_UPGRADED")
            SetLocomotorSet("SET_NORMAL");
    }

    /// <summary>
    /// Switches locomotor set (e.g. SET_PANIC, SET_FREEFALL), creating fresh locomotors as the
    /// original does. Returns false (and keeps the old set) if the object doesn't have that set.
    /// </summary>
    public bool SetLocomotorSet(string setName)
    {
        if (setName == "SET_NORMAL" && LocomotorUpgraded && locomotorSets.ContainsKey("SET_NORMAL_UPGRADED"))
            setName = "SET_NORMAL_UPGRADED";
        if (setName.Equals(LocomotorSetName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!locomotorSets.TryGetValue(setName, out var set))
            return false;
        LocomotorSetName = setName;
        currentSet.Clear();
        foreach (var template in set)
            currentSet.Add(new Locomotor(template, Game));
        CurrentLocomotor = null;
        ChooseGoodLocomotorFromCurrentSet();
        return true;
    }

    /// <summary>Picks the locomotor of the set that suits the ground under the unit (e.g. a hovercraft's water locomotor).</summary>
    void ChooseGoodLocomotorFromCurrentSet()
    {
        var previous = CurrentLocomotor;
        var best = Game.Pathfinder.ChooseBestLocomotorForPosition(currentSet, Owner.Position)
            ?? previous ?? currentSet.FirstOrDefault(l => (l.Surfaces & Surfaces.Ground) != 0) ?? currentSet.FirstOrDefault();
        CurrentLocomotor = best;
        if (best != null && best != previous)
        {
            best.UsePreciseZPos = false;
            best.NoSlowDownAsApproachingDest = false;
            best.UltraAccurate = false;
        }
    }

    /// <summary>Surfaces this unit can travel on (all locomotors in the current set combined).</summary>
    public Surfaces Surfaces => currentSet.Aggregate(Surfaces.None, (s, l) => s | l.Surfaces);

    public bool CanMove => CurrentLocomotor != null && !Owner.IsKindOf(Kind.Immobile) && !Owner.IsDisabled && CurrentLocomotor.MaxSpeedFor(Owner) > 0;

    /// <summary>On the ground under its own power (not flying, falling or carried) - the original's isDoingGroundMovement.</summary>
    public bool IsDoingGroundMovement
    {
        get
        {
            if (Owner.IsDisabledBy("DISABLED_UNMANNED") && Owner.IsKindOf("PRODUCED_AT_HELIPAD"))
                return true;
            if (Surfaces == Surfaces.Air || CurrentLocomotor == null || (CurrentLocomotor.Surfaces & Surfaces.Air) != 0)
                return false;
            if (Owner.IsDisabledBy("DISABLED_HELD"))
                return false;
            if (Owner.IsAboveTerrain && Physics is { AllowToFall: true })
                return false;
            return true;
        }
    }

    // ---- Orders ----

    public virtual void Stop(CommandSource source = CommandSource.Player)
    {
        LastCommandSource = source;
        ClearPath();
        TargetId = 0;
        State = AIState.Idle;
        Owner.SetCondition(Condition.Attacking, false);
        Owner.SetStatus(Core.Status.IsAttacking, false);
        Wake();
    }

    public virtual void MoveTo(Vector3 destination, CommandSource source = CommandSource.Player)
    {
        LastCommandSource = source;
        TargetId = 0;
        waypointRoute = null;
        BeginMove(destination);
        State = AIState.Moving;
    }

    /// <summary>Move to a point, attacking enemies met on the way.</summary>
    public void AttackMoveTo(Vector3 destination, CommandSource source = CommandSource.Player)
    {
        MoveTo(destination, source);
        State = AIState.AttackMoving;
    }

    /// <summary>Follow a list of points (map waypoint paths used by scripts).</summary>
    public void FollowWaypoints(IReadOnlyList<Vector3> points, CommandSource source = CommandSource.Script, bool asAttackMove = false)
    {
        if (points.Count == 0)
            return;
        LastCommandSource = source;
        TargetId = 0;
        waypointRoute = points.ToList();
        waypointRouteIndex = 0;
        BeginMove(points[0]);
        State = asAttackMove ? AIState.AttackMoving : AIState.FollowingPath;
    }

    /// <summary>Shots left before an attack order ends by itself (FireWeaponPower's MaxShotsToFire); -1 = no limit.</summary>
    int shotsRemaining = -1;

    public virtual void AttackObject(GameObject target, CommandSource source = CommandSource.Player, int maxShots = -1)
    {
        if (target == Owner || !Owner.Weapons.HasAnyWeapon)
            return;
        shotsRemaining = maxShots > 0 ? maxShots : -1;
        LastCommandSource = source;
        TargetId = target.Id;
        ClearPath();
        State = AIState.AttackingObject;
        Wake();
    }

    public void AttackPosition(Vector3 position, CommandSource source = CommandSource.Player, int maxShots = -1)
    {
        if (!Owner.Weapons.HasAnyWeapon)
            return;
        shotsRemaining = maxShots > 0 ? maxShots : -1;
        LastCommandSource = source;
        TargetId = 0;
        TargetPosition = position;
        ClearPath();
        State = AIState.AttackingPosition;
        Wake();
    }

    /// <summary>Walks up to an object (e.g. a hacker to its target). Ends idle when close.</summary>
    public void MoveToObject(GameObject target, CommandSource source = CommandSource.Player) => MoveTo(target.Position, source);

    /// <summary>Turns on the spot to face a point, then goes idle.</summary>
    public void FacePosition(Vector3 point, CommandSource source = CommandSource.Player)
    {
        LastCommandSource = source;
        ClearPath();
        TargetId = 0;
        TargetPosition = point;
        State = AIState.Facing;
        Wake();
    }

    public void FaceObject(GameObject target, CommandSource source = CommandSource.Player) => FacePosition(target.Position, source);

    public void Guard(Vector3 position, CommandSource source = CommandSource.Player)
    {
        LastCommandSource = source;
        guardPosition = position;
        TargetId = 0;
        BeginMove(position);
        State = AIState.Guarding;
    }

    /// <summary>Called by the body when this unit is hit: idle units fight back.</summary>
    public virtual void OnAttacked(GameObject attacker)
    {
        if (State != AIState.Idle && State != AIState.Guarding)
            return;
        if (Owner.IsKindOf("CANNOT_RETALIATE") || !Owner.Weapons.HasAnyWeapon || !Owner.Owner.IsEnemyOf(attacker.Owner))
            return;
        if (Owner.Weapons.ChooseFor(attacker) != null)
            AttackObject(attacker, CommandSource.AI);
    }

    // ---- The per-frame update ----

    public virtual int Update()
    {
        if (Owner.IsEffectivelyDead)
            return 1000;

        foreach (var turret in Turrets)
            turret.Idle(Frame);
        foreach (var weapon in Owner.Weapons.All)
            weapon.UpdateIdleReload();
        Owner.SetStatus(Core.Status.IsFiringWeapon, false);

        switch (State)
        {
            case AIState.Moving:
            case AIState.FollowingPath:
                if (FollowPath())
                    OnArrived();
                break;

            case AIState.AttackMoving:
                if (TryAutoAcquire(force: false))
                    break;
                if (FollowPath())
                    OnArrived();
                break;

            case AIState.AttackingObject:
                UpdateAttackObject();
                break;

            case AIState.AttackingPosition:
                UpdateAttackPosition();
                break;

            case AIState.Guarding:
                UpdateGuard();
                break;

            case AIState.Facing:
            {
                float angle = MathUtil.AngleTo(Owner.Position, TargetPosition);
                if (MathF.Abs(MathUtil.AngleDifference(Owner.Orientation, angle)) < 0.01f)
                {
                    Decelerate();
                    State = AIState.Idle;
                }
                else
                {
                    SetGoalAngle(angle);
                }
                break;
            }

            case AIState.Busy:
                Decelerate();
                break;

            case AIState.Idle:
                Decelerate();
                if (autoAcquireWhenIdle || Owner.IsKindOf(Kind.Structure))
                    TryAutoAcquire(force: false);
                break;
        }

        UpdateMovingCondition();
        DoLocomotor();
        return 0;
    }

    void OnArrived()
    {
        if (waypointRoute != null && waypointRouteIndex + 1 < waypointRoute.Count)
        {
            waypointRouteIndex++;
            BeginMove(waypointRoute[waypointRouteIndex]);
            return;
        }
        waypointRoute = null;
        guardPosition = Owner.Position;
        State = AIState.Idle;
        OnReachedDestination();
    }

    /// <summary>Hook for derived AIs (e.g. a dozer starting construction when it arrives).</summary>
    protected virtual void OnReachedDestination() { }

    // ---- Attacking ----

    void UpdateAttackObject()
    {
        var target = Game.FindObject(TargetId);
        if (target == null || target.IsEffectivelyDead || !IsValidTarget(target))
        {
            TargetId = 0;
            State = AIState.Idle;
            Owner.SetCondition(Condition.Attacking, false);
            Owner.SetStatus(Core.Status.IsAttacking, false);
            if (LastCommandSource == CommandSource.AI)
                guardPosition = Owner.Position;
            return;
        }

        var weapon = Owner.Weapons.ChooseFor(target);
        if (weapon == null)
        {
            Stop(LastCommandSource);
            return;
        }

        Owner.SetCondition(Condition.Attacking);
        Owner.SetStatus(Core.Status.IsAttacking);

        if (!weapon.IsInRange(target))
        {
            if (!CanMove)
            {
                // Immobile (buildings) or held units drop targets out of reach.
                if (LastCommandSource == CommandSource.AI)
                    Stop(CommandSource.AI);
                return;
            }
            if (Frame >= nextRepathFrame || path.Count == 0)
            {
                BeginMove(target.Position, keepState: true);
                nextRepathFrame = Frame + 15;
            }
            FollowPath();
            return;
        }

        ClearPath();
        Decelerate();
        if (AimAt(target.Position, weapon) && weapon.FireAt(target))
            CountShot();
    }

    void CountShot()
    {
        if (shotsRemaining > 0 && --shotsRemaining == 0)
        {
            shotsRemaining = -1;
            Stop(LastCommandSource);
        }
    }

    void UpdateAttackPosition()
    {
        var weapon = Owner.Weapons.Current;
        if (weapon == null)
        {
            Stop(LastCommandSource);
            return;
        }
        Owner.SetCondition(Condition.Attacking);
        if (!weapon.IsInRange(TargetPosition))
        {
            if (!CanMove)
                return;
            if (path.Count == 0)
                BeginMove(TargetPosition, keepState: true);
            FollowPath();
            return;
        }
        ClearPath();
        Decelerate();
        if (AimAt(TargetPosition, weapon) && weapon.FireAtPosition(TargetPosition))
            CountShot();
    }

    void UpdateGuard()
    {
        if (TryAutoAcquire(force: false, around: guardPosition, radius: guardRadius))
            return;
        if (MathUtil.Distance2D(Owner.Position, guardPosition) > 20 && path.Count == 0 && CanMove)
            BeginMove(guardPosition, keepState: true);
        if (path.Count > 0)
            FollowPath();
        else
            Decelerate();
    }

    /// <summary>Turns the turret (or body) to face a point. Returns true when aimed well enough to fire.</summary>
    protected bool AimAt(Vector3 point, Weapon weapon)
    {
        float angle = MathUtil.AngleTo(Owner.Position, point);
        float tolerance = Math.Max(weapon.Template.AcceptableAimDelta, 0.05f);

        var turret = Turrets.FirstOrDefault(t => t.Controls(weapon.Slot));
        if (turret != null)
            return turret.AimAt(angle, Owner.Orientation, tolerance, Frame);

        if (!CanMove && Owner.IsKindOf(Kind.Structure))
            return true; // buildings without turrets fire in any direction

        bool aimed = MathF.Abs(MathUtil.AngleDifference(Owner.Orientation, angle)) <= Math.Max(tolerance, 0.1f);
        if (CurrentLocomotor == null)
        {
            Owner.SetOrientation(MathUtil.TurnTowards(Owner.Orientation, angle, MathF.PI / 15f));
            return aimed;
        }
        if (aimed)
            Decelerate();
        else
            SetGoalAngle(angle);
        return aimed;
    }

    bool IsValidTarget(GameObject target)
    {
        if (target.IsKindOf(Kind.Unattackable) || target.IsContained)
            return false;
        if (target.HasStatus(Core.Status.Stealthed) && !target.HasStatus(Core.Status.Detected) && target.Owner.IsEnemyOf(Owner.Owner))
            return false;
        return true;
    }

    /// <summary>Looks for the nearest enemy within range and attacks it. Returns true if a target was found.</summary>
    protected bool TryAutoAcquire(bool force, Vector3? around = null, float radius = 0)
    {
        if (!Owner.Weapons.HasAnyWeapon || Owner.HasStatus(Core.Status.UnderConstruction) || Owner.IsDisabled)
            return false;
        if (!force && Frame < nextAutoAcquireFrame)
            return false;
        if (Owner.HasStatus(Core.Status.Stealthed) && !autoAcquireWhenStealthed && !Owner.IsKindOf(Kind.Structure))
            return false;
        nextAutoAcquireFrame = Frame + (Owner.IsKindOf(Kind.Structure) ? 10 : Math.Min(moodCheckFrames, 30));

        var center = around ?? Owner.Position;
        float range = Math.Max(Owner.Weapons.MaxRange, radius);
        if (CanMove && !Owner.IsKindOf(Kind.Structure))
            range = Math.Max(range, Owner.Template.VisionRange);

        GameObject? best = null;
        float bestDistance = float.MaxValue;
        foreach (var candidate in Game.ObjectsInRadius(center, range))
        {
            if (candidate == Owner || candidate.IsEffectivelyDead || !Owner.Owner.IsEnemyOf(candidate.Owner))
                continue;
            if (!IsValidTarget(candidate) || candidate.IsKindOf(Kind.Projectile) && !Owner.Weapons.All.Any(w => w.Template.AntiProjectile || w.Template.AntiSmallMissile))
                continue;
            if (candidate.IsKindOf(Kind.Prop) || candidate.IsKindOf(Kind.Shrubbery) || candidate.IsKindOf(Kind.Crate))
                continue;
            if (Owner.Weapons.ChooseFor(candidate) == null)
                continue;
            // Immobile shooters only consider what they can actually reach.
            if (!CanMove && !Owner.Weapons.All.Any(w => w.CanTarget(candidate) && w.IsInRange(candidate)))
                continue;

            float distance = MathUtil.DistanceSquared2D(Owner.Position, candidate.Position);
            // Prefer units over buildings, as the original target priorities roughly do.
            if (candidate.IsKindOf(Kind.Structure))
                distance *= 4;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        if (best == null)
            return false;

        var previousState = State;
        AttackObject(best, CommandSource.AI);
        if (previousState == AIState.AttackMoving)
            resumeAttackMoveAfterTarget = true;
        return true;
    }

    bool resumeAttackMoveAfterTarget;

    // ---- Movement ----
    //
    // As in the original, the AI only decides where to go: each frame DoLocomotor hands the goal
    // (a point on the path, a point, or an angle) to the locomotor, which pushes the unit with
    // forces; PhysicsBehavior then moves it.

    void BeginMove(Vector3 destination, bool keepState = false)
    {
        Destination = destination;
        ClearPath();
        if (CurrentLocomotor == null)
            return;
        var points = Game.Pathfinder.FindPath(Owner.Position, destination, Surfaces);
        if (points.Count == 0)
            return;
        path = new Path(points.Prepend(Owner.Position));
        goalType = LocomotorGoal.PositionOnPath;
        desiredSpeed = FastAsPossible;
        CurrentLocomotor.StartMove();
        Wake();
    }

    void ClearPath()
    {
        path = null;
        if (goalType == LocomotorGoal.PositionOnPath)
            goalType = LocomotorGoal.None;
    }

    void SetGoalAngle(float angle)
    {
        goalType = LocomotorGoal.Angle;
        goalData = new Vector3(angle, 0, 0);
    }

    /// <summary>Steers straight at a point (no path), e.g. a final approach.</summary>
    protected void SetGoalPositionExplicit(Vector3 position, float speed = FastAsPossible)
    {
        goalType = LocomotorGoal.PositionExplicit;
        goalData = position;
        desiredSpeed = speed;
    }

    /// <summary>Stop steering: the locomotor holds the unit where it is (planes circle, helicopters hover).</summary>
    protected void Decelerate()
    {
        if (goalType != LocomotorGoal.None)
        {
            goalType = LocomotorGoal.None;
            path = null;
        }
    }

    /// <summary>
    /// The original's distance-to-goal: along the path for ground units (or straight-line when
    /// that is shorter or nearly there), and along the flight path for aircraft.
    /// </summary>
    float LocomotorDistanceToGoal()
    {
        if (path == null || path.IsEmpty || CurrentLocomotor == null)
            return 0;
        var last = path.LastNode;
        if (CurrentLocomotor.IsCloseEnoughDist3D || Owner.IsKindOf("PROJECTILE"))
            return Vector3.Distance(Owner.Position, last);
        bool treatAsAircraft = !IsDoingGroundMovement || CurrentLocomotor.Appearance == Appearance.Hover;
        float dist = treatAsAircraft ? path.ComputeFlightDistToGoal(Owner.Position).Distance
            : path.ComputePointOnPath(Owner.Position, Game.Pathfinder, Surfaces).DistAlongPath;
        float dx = last.X - Owner.Position.X, dy = last.Y - Owner.Position.Y;
        float distSqr = dx * dx + dy * dy;
        if (treatAsAircraft)
            return dist * dist > distSqr ? MathF.Sqrt(distSqr) : dist;
        return dist < Formats.Maps.MapFile.CellSize || dist * dist < distSqr ? MathF.Sqrt(distSqr) : dist;
    }

    /// <summary>
    /// Keeps the unit moving along its path. Returns true on arrival (the original move state's
    /// test: within the locomotor's CloseEnoughDist, and within four cells of the end for ground units).
    /// </summary>
    protected bool FollowPath()
    {
        if (path == null || CurrentLocomotor == null || !CanMove)
        {
            Decelerate();
            return path == null;
        }

        // Stuck behind something for two seconds: look for a new way.
        if (isBlockedAndStuck || blockedFrames > 2 * LogicTime.FramesPerSecond)
        {
            isBlockedAndStuck = false;
            BeginMove(Destination, keepState: true);
            if (path == null)
                return true;
        }

        goalType = LocomotorGoal.PositionOnPath;
        float onPathDistToGoal = LocomotorDistanceToGoal();
        if (onPathDistToGoal < CurrentLocomotor.CloseEnoughDistance)
        {
            if (IsDoingGroundMovement)
            {
                var last = path.LastNode;
                if (new Vector2(Owner.Position.X - last.X, Owner.Position.Y - last.Y).Length() > 4 * Formats.Maps.MapFile.CellSize)
                    return false;
            }
            finalPosition = path.LastNode;
            doFinalPosition = IsDoingGroundMovement;
            Decelerate();
            return true;
        }
        return false;
    }

    /// <summary>Applies this frame's locomotor goal (the original's AIUpdateInterface::doLocomotor).</summary>
    void DoLocomotor()
    {
        if (Owner.IsKindOf(Kind.Immobile))
            return;
        ChooseGoodLocomotorFromCurrentSet();
        if (isBlocked)
            blockedFrames++;
        else
            blockedFrames = 0;
        isBlocked = false;
        bool blocked = blockedFrames > 0;
        var locomotor = CurrentLocomotor;
        if (locomotor == null)
            return;

        locomotor.SetPhysicsOptions(Owner);
        if (!(Owner.IsEffectivelyDead && !locomotor.Template.WorksWhenDead))
        {
            float maxSpeed = locomotor.MaxSpeedFor(Owner);
            float speed = desiredSpeed >= FastAsPossible || desiredSpeed > maxSpeed ? maxSpeed : desiredSpeed;
            switch (goalType)
            {
                case LocomotorGoal.PositionExplicit:
                    locomotor.MoveTowardsPosition(Owner, goalData, 0, speed, ref blocked);
                    doFinalPosition = false;
                    break;

                case LocomotorGoal.PositionOnPath when path != null:
                {
                    Vector3 goalPos;
                    float onPathDistToGoal;
                    if (!IsDoingGroundMovement)
                        (goalPos, onPathDistToGoal) = path.ComputeFlightDistToGoal(Owner.Position);
                    else
                        (goalPos, onPathDistToGoal) = path.ComputePointOnPath(Owner.Position, Game.Pathfinder, Surfaces);
                    // Units held up by others slow down smoothly, and speed up again the same way.
                    if (blocked && speed > curMaxBlockedSpeed)
                    {
                        speed = curMaxBlockedSpeed;
                        if (bumpSpeedLimit > speed)
                            bumpSpeedLimit = speed;
                        bumpSpeedLimit *= 0.95f;
                        speed = bumpSpeedLimit;
                    }
                    else
                    {
                        blocked = false;
                        if (bumpSpeedLimit < FastAsPossible)
                        {
                            if (bumpSpeedLimit < speed * 0.2f)
                                bumpSpeedLimit = speed * 0.2f;
                            bumpSpeedLimit *= 1.05f;
                        }
                        if (speed > bumpSpeedLimit)
                            speed = bumpSpeedLimit;
                    }
                    locomotor.MoveTowardsPosition(Owner, goalPos, onPathDistToGoal, speed, ref blocked);
                    doFinalPosition = false;
                    break;
                }

                case LocomotorGoal.Angle:
                    locomotor.MoveTowardsAngle(Owner, goalData.X);
                    doFinalPosition = false;
                    break;

                default:
                    if (doFinalPosition)
                    {
                        // Ease the last little bit onto the exact end point.
                        var pos = Owner.Position;
                        bool onGround = !Owner.IsAboveTerrain;
                        float dx = finalPosition.X - pos.X, dy = finalPosition.Y - pos.Y;
                        float dSqr = dx * dx + dy * dy;
                        if (dSqr < 0.25f)
                        {
                            doFinalPosition = false;
                            Owner.SetPosition(finalPosition with { Z = onGround ? Game.Terrain.GroundHeight(finalPosition) : pos.Z });
                        }
                        else
                        {
                            float dist = Math.Max(1, MathF.Sqrt(dSqr));
                            pos.X += 2 * Formats.Maps.MapFile.CellSize * dx / (dist * LogicTime.FramesPerSecond);
                            pos.Y += 2 * Formats.Maps.MapFile.CellSize * dy / (dist * LogicTime.FramesPerSecond);
                            if (onGround)
                                pos.Z = Game.Terrain.GroundHeight(pos);
                            Owner.SetPosition(pos);
                        }
                    }
                    locomotor.MaintainCurrentPosition(Owner);
                    break;
            }
        }
        if (!blocked && blockedFrames > 1)
            blockedFrames = 1;
        Owner.SetStatus(Core.Status.AirborneTarget, Owner.HeightAboveTerrain > locomotor.Template.AirborneTargetingHeight);
        curMaxBlockedSpeed = FastAsPossible;
    }

    // ---- Bumping into other units ----

    /// <summary>
    /// Physics found this unit touching another. Returns whether physics should push them apart.
    /// Moving units that are blocked slow down (and may ask infantry to step aside); units standing
    /// on top of each other spread out.
    /// </summary>
    public bool ProcessCollision(PhysicsBehavior physics, GameObject other)
    {
        if (ignoreCollisionsUntil > Frame || other.AI is not { } otherAi)
            return false;
        if (!IsDoingGroundMovement || !otherAi.IsDoingGroundMovement)
            return false;
        bool selfMoving = IsMoving, otherMoving = otherAi.IsMoving;
        if (selfMoving)
        {
            if (BlockedBy(other))
            {
                if (Owner.IsKindOf(Kind.Infantry) && State == AIState.Busy)
                    return true;
                isBlocked = true;
                curMaxBlockedSpeed = Math.Min(curMaxBlockedSpeed, MaxBlockedSpeed(other));
                if (otherAi.moveOutOfWayId != Owner.Id && other.IsKindOf(Kind.Infantry) && !Owner.IsKindOf(Kind.Infantry))
                {
                    if (!other.HasStatus(Core.Status.UsingAbility) && !otherAi.IsBusy)
                        otherAi.MoveAwayFrom(Owner);
                    return false;
                }
                if (blockedFrames == 0)
                    blockedFrames = 1;
                if (!otherMoving)
                    isBlockedAndStuck = true;
            }
            return false;
        }

        if (Owner.IsEffectivelyDead && Owner.IsKindOf(Kind.Infantry) && other.CanCrushOrSquish(Owner, crushOnly: false))
            return true;
        // Two idle units on top of each other: both step to a free spot.
        float dx = Owner.Position.X - other.Position.X, dy = Owner.Position.Y - other.Position.Y;
        const float Cell = Formats.Maps.MapFile.CellSize;
        if (!otherMoving && dx * dx + dy * dy < Cell * Cell * 0.25f)
        {
            if (State == AIState.Busy || Owner.HasStatus(Core.Status.UsingAbility))
                return false;
            if (IsIdle && Game.Pathfinder.NearestPassable(Owner.Position, Surfaces) is { } safe)
                MoveTo(safe, CommandSource.AI);
            if (otherAi.IsIdle && Game.Pathfinder.NearestPassable(other.Position, otherAi.Surfaces) is { } otherSafe)
                otherAi.MoveTo(otherSafe, CommandSource.AI);
        }
        return false;
    }

    /// <summary>Who gives way (the original's hasHigherPathPriority): dozers, then vehicles over infantry, then by direction.</summary>
    bool HasHigherPathPriority(AIUpdateInterface other)
    {
        var them = other.Owner;
        if (Owner.IsKindOf("DOZER") != them.IsKindOf("DOZER"))
            return Owner.IsKindOf("DOZER");
        if (Owner.IsKindOf("VEHICLE") && them.IsKindOf(Kind.Infantry))
            return true;
        if (Owner.IsKindOf(Kind.Infantry) && them.IsKindOf("VEHICLE"))
            return false;
        var ourDir = MathUtil.Forward(Owner.Orientation);
        var otherDir = MathUtil.Forward(them.Orientation);
        if (ourDir.X * otherDir.X + ourDir.Y * otherDir.Y <= 0)
            return Owner.Id < them.Id;
        var combined = ourDir + otherDir;
        var toOther = them.Position - Owner.Position;
        float dot = combined.X * toOther.X + combined.Y * toOther.Y;
        return dot > 0 ? false : dot < 0 || Owner.Id < them.Id;
    }

    /// <summary>How fast we may drive into a unit that is moving away from us.</summary>
    float MaxBlockedSpeed(GameObject other)
    {
        var ourDir = MathUtil.Forward(Owner.Orientation);
        var otherDir = MathUtil.Forward(other.Orientation);
        var toOther = new Vector2(other.Position.X - Owner.Position.X, other.Position.Y - Owner.Position.Y);
        if (toOther.LengthSquared() > 0)
            toOther = Vector2.Normalize(toOther);
        float speedFactor = toOther.X * otherDir.X + toOther.Y * otherDir.Y;
        if (speedFactor < 0)
            return 0;
        if (other.FindModule<PhysicsBehavior>() is not { } otherPhysics)
            return curMaxBlockedSpeed;
        var otherVel = otherPhysics.Velocity with { Z = 0 };
        float awaySpeed = otherVel.Length() * speedFactor;
        float dot = toOther.X * ourDir.X + toOther.Y * ourDir.Y;
        if (dot <= 0)
            return curMaxBlockedSpeed;
        return Math.Min(curMaxBlockedSpeed, awaySpeed / dot);
    }

    /// <summary>Whether another unit is in our way (the original's blockedBy, without formations).</summary>
    bool BlockedBy(GameObject other)
    {
        var pos = Owner.Position;
        const float Cell = Formats.Maps.MapFile.CellSize;
        if (MathF.Abs(Destination.X - pos.X) < Cell && MathF.Abs(Destination.Y - pos.Y) < Cell)
            return false;
        if (Owner.CanCrushOrSquish(other, crushOnly: false))
            return false;
        if (other.AI is not { } otherAi || !otherAi.IsDoingGroundMovement)
            return false;
        if (CurrentLocomotor is { IsMovingBackwards: true })
            return false;
        bool otherMoving = otherAi.goalType != LocomotorGoal.None;
        var otherPos = other.Position;
        float dx = pos.X - otherPos.X, dy = pos.Y - otherPos.Y;
        float curDSqr = dx * dx + dy * dy;
        var ourDir = MathUtil.Forward(Owner.Orientation);
        var theirDir = MathUtil.Forward(other.Orientation);
        float dot = ourDir.X * theirDir.X + ourDir.Y * theirDir.Y;
        if (Owner.IsKindOf(Kind.Infantry) && other.IsKindOf(Kind.Infantry) && dot <= 0.25f)
            return false;
        if (curDSqr < Cell * Cell * 0.0001f)
            return HasHigherPathPriority(otherAi);
        if (blockedFrames > LogicTime.FramesPerSecond && dot <= 0)
            return false;
        float collisionAngle = MathUtil.AngleDifference(Owner.Orientation, MathUtil.AngleTo(pos, otherPos));
        float otherAngle = MathUtil.AngleDifference(other.Orientation, MathUtil.AngleTo(otherPos, pos));
        if (collisionAngle > MathF.PI / 2 || collisionAngle < -MathF.PI / 2)
            return false;
        float angleLimit = MathF.PI / 4 * (otherMoving ? 1 : 0.75f);
        if (collisionAngle > angleLimit || collisionAngle < -angleLimit)
        {
            if (dot <= 0)
                return false;
            if (!otherMoving || otherAngle <= angleLimit && otherAngle >= -angleLimit)
                return false;
            dx += ourDir.X - theirDir.X;
            dy += ourDir.Y - theirDir.Y;
            if (curDSqr > dx * dx + dy * dy)
                return !HasHigherPathPriority(otherAi);
            return false;
        }
        return !otherAi.Owner.IsEffectivelyDead;
    }

    /// <summary>Steps aside to let a vehicle through (the original's move-out-of-the-way state, simplified to one step).</summary>
    void MoveAwayFrom(GameObject unit)
    {
        if (Owner.IsEffectivelyDead || !Owner.IsMobile || moveOutOfWayId == unit.Id)
            return;
        moveOutOfWayId = unit.Id;
        var away = MathUtil.Forward(unit.Orientation);
        var side = new Vector3(-away.Y, away.X, 0);
        var offset = Owner.Position - unit.Position;
        if (Vector3.Dot(offset, side) < 0)
            side = -side;
        var target = Owner.Position + side * (3 * Formats.Maps.MapFile.CellSize) + away * Formats.Maps.MapFile.CellSize;
        if (Game.Pathfinder.NearestPassable(target, Surfaces) is { } free)
        {
            var previous = State;
            MoveTo(free, CommandSource.AI);
            if (previous != AIState.Idle)
                State = AIState.Moving;
        }
    }

    void UpdateMovingCondition()
    {
        bool moving = goalType != LocomotorGoal.None && blockedFrames <= LogicTime.FramesPerSecond / 4;
        Owner.SetCondition(Condition.Moving, moving);
        if (State == AIState.Idle)
        {
            moveOutOfWayId = 0;
            if (resumeAttackMoveAfterTarget)
            {
                resumeAttackMoveAfterTarget = false;
                AttackMoveTo(Destination, LastCommandSource);
            }
        }
    }

    protected void Wake() => Owner.WakeUpdate(this);

    /// <summary>Instantly places the unit (used when spawning at a factory door or teleporting).</summary>
    public void Teleport(Vector3 position)
    {
        float z = Game.Terrain.LayerHeight(position.X, position.Y);
        if (CurrentLocomotor is { } locomotor && locomotor.Template.IsAirborne)
            z = Game.Terrain.GroundHeight(position) + locomotor.PreferredHeight;
        Owner.SetPosition(position with { Z = z });
        ClearPath();
        goalType = LocomotorGoal.None;
        Physics?.ResetDynamicPhysics();
    }
}
