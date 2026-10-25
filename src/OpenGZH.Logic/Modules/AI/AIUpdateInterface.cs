using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Locomotion;
using Path = OpenGZH.Logic.Locomotion.Path;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>What a unit's AI is currently doing.</summary>
public enum AIState { Idle, Moving, AttackingObject, AttackingPosition, AttackMoving, Guarding, FollowingPath, Busy, Facing, Entering, Rappelling, MoveAndEvacuate, MoveAndEvacuateAndExit, MoveAndDelete, Docking }

/// <summary>
/// The standard unit brain ("AIUpdateInterface"): moves along paths using the object's locomotor,
/// attacks targets with its weapons and turrets, guards areas, and automatically engages enemies
/// when idle. Specialised AIs (dozers, harvesters, aircraft, ...) derive from this class.
/// </summary>
public partial class AIUpdateInterface : Module, IUpdateModule, ICreateModule
{
    public AIState State { get; protected set; } = AIState.Idle;

    // AIs steer before physics moves anything, and keep working inside transports (DISABLED_HELD).
    public UpdatePhase Phase => UpdatePhase.Initial;
    public FlagSet DisabledTypesToProcess => DisabledMask.Held;
    public CommandSource LastCommandSource { get; protected set; } = CommandSource.AI;

    public virtual bool IsIdle => State == AIState.Idle && scriptMode == ScriptMode.None;

    /// <summary>
    /// Every order passes through here first (the original's aiDoCommand). A derived AI can react
    /// to it, or hold it back until it is ready (a hacker packs up its laptop first): return true to
    /// take the order, and replay it later by invoking <paramref name="replay"/>.
    /// </summary>
    protected virtual bool OnOrder(AIOrder order, Action replay) => false;

    /// <summary>
    /// Chase targets out of weapon range even when the AI picked them itself. A human player's
    /// units normally don't (they would wander off); Troop Crawler passengers do.
    /// </summary>
    public bool AllowedToChase { get; set; }

    // The current target was picked up while idle (the chase rule above applies to those).
    bool idleAcquired;
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
    /// <summary>When the last path was planned, and the earliest a blocked unit may plan again.</summary>
    int pathTimestamp = -1000, queuedPathFrame;
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
                Turrets.Add(new Turret(turret.IsSequence ? turret.Items[0] : turret, turretKey == "AltTurret"));

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
    public virtual bool SetLocomotorSet(string setName)
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
    public virtual bool IsDoingGroundMovement
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
        if (OnOrder(new AIOrder(AIOrderType.Stop, source, null), () => Stop(source)))
            return;
        ResetToIdle(source);
    }

    /// <summary>Drops whatever it was doing (Stop without passing through <see cref="OnOrder"/>).</summary>
    protected void ResetToIdle(CommandSource source)
    {
        NoteOrder();
        LastCommandSource = source;
        EnterTargetId = 0;
        ClearPath();
        TargetId = 0;
        State = AIState.Idle;
        Owner.SetCondition(Condition.Attacking, false);
        Owner.SetStatus(Core.Status.IsAttacking, false);
        Wake();
    }

    public virtual void MoveTo(Vector3 destination, CommandSource source = CommandSource.Player)
    {
        if (OnOrder(new AIOrder(AIOrderType.MoveTo, source, destination), () => MoveTo(destination, source)))
            return;
        NoteOrder();
        LastCommandSource = source;
        TargetId = 0;
        waypointRoute = null;
        BeginMove(destination);
        State = AIState.Moving;
    }

    /// <summary>Move to a point, attacking enemies met on the way.</summary>
    public void AttackMoveTo(Vector3 destination, CommandSource source = CommandSource.Player)
    {
        if (OnOrder(new AIOrder(AIOrderType.AttackMoveTo, source, destination), () => AttackMoveTo(destination, source)))
            return;
        MoveTo(destination, source);
        State = AIState.AttackMoving;
    }

    /// <summary>Follow a list of points (map waypoint paths used by scripts).</summary>
    public void FollowWaypoints(IReadOnlyList<Vector3> points, CommandSource source = CommandSource.Script, bool asAttackMove = false)
    {
        if (OnOrder(new AIOrder(AIOrderType.FollowWaypoints, source, points.Count > 0 ? points[^1] : null), () => FollowWaypoints(points, source, asAttackMove)))
            return;
        if (points.Count == 0)
            return;
        NoteOrder();
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
        if (OnOrder(new AIOrder(AIOrderType.AttackObject, source, target.Position, target.Id), () => AttackObject(target, source, maxShots)))
            return;
        if (target == Owner || !Owner.Weapons.HasAnyWeapon)
            return;
        NoteOrder();
        idleAcquired = false;
        shotsRemaining = maxShots > 0 ? maxShots : -1;
        LastCommandSource = source;
        TargetId = target.Id;
        ClearPath();
        State = AIState.AttackingObject;
        Wake();
    }

    /// <summary>Attackers of a destroyed GLA building go on attacking its rebuild hole (the original's transferAttack).</summary>
    public void TransferAttack(uint fromId, uint toId)
    {
        if (TargetId == fromId && TargetId != 0)
            TargetId = toId;
    }

    public virtual void AttackPosition(Vector3 position, CommandSource source = CommandSource.Player, int maxShots = -1)
    {
        if (OnOrder(new AIOrder(AIOrderType.AttackPosition, source, position), () => AttackPosition(position, source, maxShots)))
            return;
        if (!Owner.Weapons.HasAnyWeapon)
            return;
        NoteOrder();
        shotsRemaining = maxShots > 0 ? maxShots : -1;
        LastCommandSource = source;
        TargetId = 0;
        TargetPosition = position;
        ClearPath();
        State = AIState.AttackingPosition;
        Wake();
    }

    // ---- Getting into and out of containers ----

    /// <summary>The container this unit is walking to and wants to get into (0 = none).</summary>
    public uint EnterTargetId { get; private set; }
    bool exitPending;

    /// <summary>Set by transports' GoAggressiveOnExit (the original's aggressive attitude).</summary>
    public bool Aggressive { get; set; }

    /// <summary>Walk to a transport or building and get in.</summary>
    public void Enter(GameObject container, CommandSource source = CommandSource.Player)
    {
        if (OnOrder(new AIOrder(AIOrderType.Enter, source, container.Position, container.Id), () => Enter(container, source)))
            return;
        if (Owner.IsContained)
            return;
        NoteOrder();
        // Hijackers, terrorists and saboteurs "enter" their target by touching it (their CrateCollide does the rest).
        if (container.Contain == null && !CanEnterByTouch(container))
            return;
        touchEnter = CanEnterByTouch(container);
        MoveTo(container.Position, source);
        EnterTargetId = container.Id;
        IgnoreObstacle(container);
        State = AIState.Entering;
    }

    /// <summary>
    /// Targets "entered" by walking into them: crate-like abilities (hijack, sabotage, car bomb), and
    /// infantry climbing into a sniped, unmanned vehicle.
    /// </summary>
    bool touchEnter;

    bool CanEnterByTouch(GameObject target) =>
        Owner.FindModules<Collide.CrateCollide>().Any(c => c.IsValidToExecute(target))
        || (Owner.IsKindOf(Kind.Infantry) && target.IsDisabledBy("DISABLED_UNMANNED"));

    /// <summary>Called by the container once the unit is inside.</summary>
    public void OnEnteredContainer()
    {
        EnterTargetId = 0;
        IgnoreObstacle(null);
        ClearPath();
        goalType = LocomotorGoal.None;
        State = AIState.Idle;
        Physics?.ResetDynamicPhysics();
    }

    /// <summary>Get out of the container through its door, when the door is free.</summary>
    public void ExitContainer(CommandSource source = CommandSource.Player)
    {
        if (OnOrder(new AIOrder(AIOrderType.ExitContainer, source, null), () => ExitContainer(source)))
            return;
        if (!Owner.IsContained)
            return;
        LastCommandSource = source;
        exitPending = true;
        Wake();
    }

    /// <summary>Get out right now, whether the door is busy or not.</summary>
    public void ExitContainerInstantly(CommandSource source = CommandSource.Player)
    {
        if (Game.FindObject(Owner.ContainedById)?.Contain is { } contain)
        {
            LastCommandSource = source;
            exitPending = false;
            contain.ExitObjectViaDoor(Owner);
        }
    }

    void UpdateExit()
    {
        if (!exitPending)
            return;
        if (Game.FindObject(Owner.ContainedById)?.Contain is not { } contain)
        {
            exitPending = false;
            return;
        }
        if (contain.IsExitBusy || !contain.ReserveDoorForExit(Owner))
            return;
        exitPending = false;
        contain.ExitObjectViaDoor(Owner);
    }

    void UpdateEnter()
    {
        var container = Game.FindObject(EnterTargetId);
        if (container != null && touchEnter && !container.IsEffectivelyDead && CanEnterByTouch(container))
        {
            // Walk until we touch it; the collision does the deed.
            if (Frame >= nextRepathFrame && MathUtil.Distance2D(Destination, container.Position) > Formats.Maps.MapFile.CellSize * 2)
            {
                BeginMove(container.Position, keepState: true);
                nextRepathFrame = Frame + 15;
            }
            if (FollowPath() && MathUtil.Distance2D(Owner.Position, container.Position) > Owner.BoundingCircleRadius + container.BoundingCircleRadius + Formats.Maps.MapFile.CellSize)
                BeginMove(container.Position, keepState: true);
            return;
        }
        if (container?.Contain is not { } contain || container.IsEffectivelyDead || !contain.IsValidContainerFor(Owner, true))
        {
            EnterTargetId = 0;
            IgnoreObstacle(null);
            Stop(LastCommandSource);
            return;
        }
        // Units reach the container's edge (paths stop outside buildings), so touching counts from there.
        float reach = Owner.BoundingCircleRadius + container.BoundingCircleRadius + Formats.Maps.MapFile.CellSize;
        if (MathUtil.Distance2D(Owner.Position, container.Position) <= reach)
        {
            contain.TryEnter(Owner);
            return;
        }
        // Moving containers (transports) are followed.
        if (Frame >= nextRepathFrame && MathUtil.Distance2D(Destination, container.Position) > Formats.Maps.MapFile.CellSize * 2)
        {
            BeginMove(container.Position, keepState: true);
            nextRepathFrame = Frame + 15;
        }
        if (FollowPath() && MathUtil.Distance2D(Owner.Position, container.Position) <= reach * 1.5f)
            contain.TryEnter(Owner);
    }

    /// <summary>A passenger that has been told to get out and is waiting for the door.</summary>
    public bool IsWaitingToExit => exitPending;

    /// <summary>Everyone out (the original's aiEvacuate). Chinooks land first.</summary>
    public void Evacuate(CommandSource source = CommandSource.Player, bool instantly = false)
    {
        if (OnOrder(new AIOrder(AIOrderType.Evacuate, source, null), () => Evacuate(source, instantly)))
            return;
        Owner.Contain?.OrderAllPassengersToExit(source, instantly);
    }

    Vector3 evacuateOrigin;

    /// <summary>
    /// Drive (or fly) to a point and let everybody out; with <paramref name="andExit"/>, then go back
    /// where it came from and disappear (script reinforcements).
    /// </summary>
    public void MoveToAndEvacuate(Vector3 position, CommandSource source = CommandSource.Script, bool andExit = false)
    {
        var type = andExit ? AIOrderType.MoveToAndEvacuateAndExit : AIOrderType.MoveToAndEvacuate;
        if (OnOrder(new AIOrder(type, source, position), () => MoveToAndEvacuate(position, source, andExit)))
            return;
        if (!Owner.IsMobile)
            return;
        NoteOrder();
        if (andExit && this is DeliverPayloadAIUpdate payload)
        {
            payload.DeliverPayloadViaModuleData(position);
            return;
        }
        LastCommandSource = source;
        TargetId = 0;
        evacuateOrigin = Owner.Position;
        BeginMove(position, keepState: true);
        State = andExit ? AIState.MoveAndEvacuateAndExit : AIState.MoveAndEvacuate;
    }

    void UpdateMoveAndEvacuate()
    {
        if (!FollowPath() && path != null)
            return;
        Owner.Contain?.OrderAllPassengersToExit(CommandSource.AI, instantly: false);
        if (State == AIState.MoveAndEvacuateAndExit)
        {
            BeginMove(evacuateOrigin, keepState: true);
            path?.Append(evacuateOrigin with { Z = Game.Terrain.GroundHeight(evacuateOrigin) });
            State = AIState.MoveAndDelete;
        }
        else
            State = AIState.Idle;
    }

    void UpdateMoveAndDelete()
    {
        if (CurrentLocomotor != null)
            CurrentLocomotor.AllowInvalidPosition = true;
        if (FollowPath() || path == null)
            Game.DestroyObject(Owner);
    }

    /// <summary>A transport drops its troops on a building or point (Chinooks rappel them; others just unload).</summary>
    public virtual void CombatDrop(GameObject? target, Vector3 position, CommandSource source = CommandSource.Player)
    {
        if (OnOrder(new AIOrder(AIOrderType.CombatDrop, source, position, target?.Id ?? 0), () => CombatDrop(target, position, source)))
            return;
        Owner.Contain?.RemoveAllContained();
    }

    // ---- Rappelling down a rope (Rangers from a Chinook) ----

    uint rappelTargetId;
    bool rappelTargetIsBuilding;
    float rappelDestZ, rappelRate;

    public void SetDesiredSpeed(float speed) => desiredSpeed = speed;

    public bool IsInRappel() => State == AIState.Rappelling;

    /// <summary>Attacking with an anti-mine weapon (whatever the target): mines don't go off under it.</summary>
    public bool IsClearingMines => Owner.HasStatus(Core.Status.IsAttacking) && Owner.Weapons.Current is { Template.AntiMine: true };
    public float DesiredSpeed => desiredSpeed;

    public void RappelInto(GameObject? target, Vector3 position, CommandSource source = CommandSource.AI)
    {
        if (OnOrder(new AIOrder(AIOrderType.RappelInto, source, position, target?.Id ?? 0), () => RappelInto(target, position, source)))
            return;
        ClearPath();
        LastCommandSource = source;
        blockedFrames = 0;
        isBlocked = isBlockedAndStuck = false;
        TargetPosition = position;
        if (!Owner.IsKindOf("CAN_RAPPEL"))
        {
            State = AIState.Idle;
            return;
        }
        Owner.SetCondition("RAPPELLING");
        Physics?.ResetDynamicPhysics();
        rappelTargetId = target?.Id ?? 0;
        rappelTargetIsBuilding = target is { IsEffectivelyDead: false } && target.IsKindOf(Kind.Structure);
        rappelDestZ = Game.Terrain.LayerHeight(Owner.Position.X, Owner.Position.Y);
        if (rappelTargetIsBuilding)
            rappelDestZ += target!.Geometry.MaxHeightAbovePosition;
        float maxRate = MathF.Abs(Game.Catalog.GameData.Gravity) * LogicTime.FramesPerSecond * 2.5f;
        rappelRate = -Math.Min(desiredSpeed, maxRate);
        State = AIState.Rappelling;
    }

    void UpdateRappel()
    {
        var bldg = Game.FindObject(rappelTargetId);
        if (rappelTargetIsBuilding && (bldg == null || bldg.IsEffectivelyDead))
        {
            rappelTargetIsBuilding = false;
            rappelDestZ = Game.Terrain.GroundHeight(Owner.Position);
        }
        Physics?.ScrubVelocity2D(0);
        Physics?.ScrubVelocityZ(rappelRate);
        if (!rappelTargetIsBuilding)
            rappelDestZ = Game.Terrain.LayerHeight(Owner.Position.X, Owner.Position.Y);
        if (Owner.Position.Z > rappelDestZ)
            return;

        Owner.SetPosition(Owner.Position with { Z = rappelDestZ });
        FinishRappel();
        if (rappelTargetIsBuilding && bldg != null)
        {
            // Landing on a building: clear out up to two enemies inside; killing two costs the rappeller.
            const int MaxToKill = 2;
            int killed = 0;
            while (killed < MaxToKill && bldg.Contain?.Passengers.FirstOrDefault(p => !p.IsEffectivelyDead && Owner.Owner.IsEnemyOf(p.Owner)) is { } enemy)
            {
                bldg.Contain.RemoveFromContain(enemy);
                enemy.Kill("NORMAL", Owner.Id);
                killed++;
            }
            if (killed > 0 && Owner.Template.UnitFx("CombatDropKillFX") is { Length: > 0 } fx)
                Game.Events.PlayFx(fx, bldg.Position, bldg);
            if (killed == MaxToKill)
            {
                Owner.Kill();
                return;
            }
            if (bldg.Contain is { } contain && contain.IsValidContainerFor(Owner, true))
            {
                contain.AddToContain(Owner);
                return;
            }
            // Can't get in: come down beside it and walk away from it.
            float offset = Math.Min(Owner.BoundingCircleRadius, bldg.BoundingCircleRadius);
            float angle = Game.Random.Float(MathF.PI, 2 * MathF.PI);
            var start = bldg.Position + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * offset;
            start.Z = Game.Terrain.GroundHeight(start);
            Owner.SetPosition(start);
            Owner.SetOrientation(bldg.Orientation);
            if (Game.Pathfinder.NearestPassable(start, Surfaces) is { } end)
                FollowExitProductionPath([end], bldg);
        }
    }

    void FinishRappel()
    {
        Owner.SetCondition("RAPPELLING", false);
        desiredSpeed = FastAsPossible;
        State = AIState.Idle;
    }

    /// <summary>Whether a passenger may get out of this unit now (planes say no until their doors are open).</summary>
    public virtual bool IsFreeToExit(GameObject rider) => true;

    /// <summary>Hackers start hacking the internet when they get into an Internet Center (HackInternetAIUpdate does it).</summary>
    public virtual void HackInternet(CommandSource source) { }

    /// <summary>Look for targets again straight away (units leaving a transport).</summary>
    public void WakeAndAttemptToTarget()
    {
        nextAutoAcquireFrame = Frame;
        Wake();
    }

    /// <summary>Inside a container: may this unit shoot out of it?</summary>
    protected bool CanFireFromContainer =>
        Game.FindObject(Owner.ContainedById)?.Contain is { } container && container.IsPassengerAllowedToFire(Owner);

    /// <summary>Disabled for attacking purposes (held inside a container that lets it shoot doesn't count).</summary>
    bool IsDisabledForAttack =>
        Owner.IsDisabled && !(Owner.IsContained && CanFireFromContainer && Owner.DisabledFlags.HasAll(DisabledMask.Held) && Owner.DisabledFlags.Count == 1);

    /// <summary>Walks up to an object (e.g. a hacker to its target). Ends idle when close.</summary>
    public void MoveToObject(GameObject target, CommandSource source = CommandSource.Player) => MoveTo(target.Position, source);

    /// <summary>Turns on the spot to face a point, then goes idle.</summary>
    public void FacePosition(Vector3 point, CommandSource source = CommandSource.Player)
    {
        if (OnOrder(new AIOrder(AIOrderType.Face, source, point), () => FacePosition(point, source)))
            return;
        NoteOrder();
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
        if (OnOrder(new AIOrder(AIOrderType.Guard, source, position), () => Guard(position, source)))
            return;
        NoteOrder();
        AreaToGuard = null;
        guardRadius = 150f;
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

        UpdateExit();
        if (Owner.IsContained && !CanFireFromContainer)
        {
            // Riders that can't shoot out just wait (their container moves them).
            if (State is AIState.AttackingObject or AIState.AttackingPosition)
                Stop(CommandSource.AI);
            return 0;
        }

        if (UpdateDerivedState())
        {
            holdBusyThisFrame = false;
            UpdateMovingCondition();
            DoLocomotor();
            return 0;
        }

        if (holdBusyThisFrame)
        {
            // A temporary BUSY state for this frame only (deploy-style units packing or unpacking).
            holdBusyThisFrame = false;
            goalType = LocomotorGoal.None; // stop steering, but keep the path for afterwards
            UpdateMovingCondition();
            DoLocomotor();
            return 0;
        }

        long profileStart = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var profileState = State;
        switch (State)
        {
            case AIState.Entering:
                UpdateEnter();
                break;

            case AIState.Docking:
                UpdateDock();
                break;

            case AIState.Rappelling:
                UpdateRappel();
                break;

            case AIState.MoveAndEvacuate:
            case AIState.MoveAndEvacuateAndExit:
                UpdateMoveAndEvacuate();
                break;

            case AIState.MoveAndDelete:
                UpdateMoveAndDelete();
                break;

            case AIState.Moving:
            case AIState.FollowingPath:
                if (FollowPath())
                    OnArrived();
                break;

            case AIState.AttackMoving:
                // A jet out of its base-reload ammo ends the attack-move to go and reload.
                if (this is JetAIUpdate { IsOutOfSpecialReloadAmmo: true })
                {
                    State = AIState.Idle;
                    break;
                }
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
                if (scriptMode != ScriptMode.None && UpdateScriptModeWhileIdle())
                    break;
                Decelerate();
                if (autoAcquireWhenIdle || Owner.IsKindOf(Kind.Structure))
                    TryAutoAcquire(force: false);
                break;
        }
        if (GameProfiler.Enabled)
        {
            GameProfiler.Add("ai:" + profileState, profileStart);
            profileStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        UpdateMovingCondition();
        DoLocomotor();
        if (GameProfiler.Enabled)
            GameProfiler.Add("ai:locomotor", profileStart);
        return 0;
    }

    bool holdBusyThisFrame;

    /// <summary>Keep the current order but do nothing with it this frame (the original's temporary AI_BUSY state).</summary>
    protected void HoldBusyThisFrame() => holdBusyThisFrame = true;

    /// <summary>Has a path it is following (the original's getPath() || isWaitingForPath()).</summary>
    protected bool IsTryingToMove => path != null;

    public bool IsInAttackState => State is AIState.AttackingObject or AIState.AttackingPosition;

    /// <summary>Guarding with nothing to shoot and nowhere to go.</summary>
    public bool IsInGuardIdleState => State == AIState.Guarding && path == null;

    public GameObject? CurrentVictim => State == AIState.AttackingObject ? Game.FindObject(TargetId) : null;
    public Vector3? CurrentVictimPosition => State == AIState.AttackingPosition ? TargetPosition : null;

    /// <summary>The turret that aims the current weapon, if any.</summary>
    public Turret? TurretForCurrentWeapon =>
        Owner.Weapons.Current is { } weapon ? Turrets.FirstOrDefault(t => t.Controls(weapon.Slot)) : null;

    void OnArrived()
    {
        if (OnScriptArrived())
            return;
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
        if (AttackFailsForAmmo())
            return;
        var target = Game.FindObject(TargetId);
        if (target == null || target.IsEffectivelyDead || !IsValidTarget(target))
        {
            if (target == null || target.IsEffectivelyDead)
                OnVictimDead();
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

        if (Game.FindObject(Owner.ContainedById)?.Contain is { IsGarrisonable: true } garrison)
            garrison.AttemptBestFirePointPosition(Owner, weapon, target, null);

        if (!weapon.IsInRange(target))
        {
            // A human player's unit doesn't chase what it picked up by itself (unless allowed to).
            if (idleAcquired && LastCommandSource == CommandSource.AI && Owner.Owner.IsHuman && !AllowedToChase && !weapon.Template.IsContactWeapon)
            {
                Stop(CommandSource.AI);
                return;
            }
            if (!CanMove)
            {
                // Immobile (buildings) or held units drop targets out of reach.
                if (LastCommandSource == CommandSource.AI)
                    Stop(CommandSource.AI);
                return;
            }
            if (Frame >= nextRepathFrame || path == null)
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

    /// <summary>
    /// All its weapons are empty and won't reload by themselves (the Raptor): the attack fails, as
    /// the original's attack state does.
    /// </summary>
    bool AttackFailsForAmmo()
    {
        if (!Owner.Weapons.IsOutOfAmmo || Owner.IsKindOf(Kind.Projectile))
            return false;
        TargetId = 0;
        State = AIState.Idle;
        Owner.SetCondition(Condition.Attacking, false);
        Owner.SetStatus(Core.Status.IsAttacking, false);
        return true;
    }

    void UpdateAttackPosition()
    {
        if (AttackFailsForAmmo())
            return;
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
            if (path == null)
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
        UpdateGuardedObject();
        if (TryAutoAcquire(force: false, around: guardPosition, radius: guardRadius))
            return;
        if (MathUtil.Distance2D(Owner.Position, guardPosition) > 20 && path == null && CanMove)
            BeginMove(guardPosition, keepState: true);
        if (path != null)
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
        if (target.IsKindOf(Kind.Unattackable) || target.IsContained || target.HasStatus("MASKED") || target.IsRemovedFromPartition)
            return false;
        if (target.HasStatus(Core.Status.Stealthed) && !target.HasStatus(Core.Status.Detected) && target.Owner.IsEnemyOf(Owner.Owner))
            return false;
        return true;
    }

    /// <summary>Looks for the nearest enemy within range and attacks it. Returns true if a target was found.</summary>
    protected bool TryAutoAcquire(bool force, Vector3? around = null, float radius = 0)
    {
        if (!Owner.Weapons.HasAnyWeapon || Owner.HasStatus(Core.Status.UnderConstruction) || IsDisabledForAttack)
            return false;
        if (!force && Frame < nextAutoAcquireFrame)
            return false;
        if (Owner.HasStatus(Core.Status.Stealthed) && !autoAcquireWhenStealthed && !Owner.IsKindOf(Kind.Structure))
            return false;
        nextAutoAcquireFrame = Frame + (Owner.IsKindOf(Kind.Structure) ? 10 : Math.Min(moodCheckFrames, 30));

        bool fromIdle = State == AIState.Idle;
        var best = ScanForMoodTarget(around, radius);
        if (best == null)
            return false;

        var previousState = State;
        Internal(() => AttackObject(best, CommandSource.AI));
        idleAcquired = fromIdle;
        if (previousState == AIState.AttackMoving)
            resumeAttackMoveAfterTarget = true;
        return true;
    }

    /// <summary>The enemy this unit's mood would attack now, without attacking it (the original's getNextMoodTarget).</summary>
    public GameObject? GetNextMoodTarget()
    {
        if (!Owner.Weapons.HasAnyWeapon || Owner.HasStatus(Core.Status.UnderConstruction) || IsDisabledForAttack)
            return null;
        return ScanForMoodTarget(null, 0);
    }

    GameObject? ScanForMoodTarget(Vector3? around, float radius)
    {
        var center = around ?? Owner.Position;
        float moodScale = MoodRangeScale();
        if (moodScale <= 0 && State == AIState.Idle && !Owner.IsKindOf(Kind.Structure))
            return null;
        float range = Math.Max(Owner.Weapons.MaxRange, radius) * Math.Max(1, moodScale);
        bool fromIdle = State == AIState.Idle;
        bool mayChase = !(fromIdle && Owner.Owner.IsHuman && !AllowedToChase);
        if (CanMove && !Owner.IsKindOf(Kind.Structure) && mayChase)
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
        return best;
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
        pathTimestamp = Frame;
        long profileStart = GameProfiler.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var points = Game.Pathfinder.FindLayeredPath(Owner.Position, destination, Surfaces, Owner.Layer);
        if (GameProfiler.Enabled)
            GameProfiler.Add("ai:findpath " + Owner.Template.Name, profileStart);
        if (points.Count == 0)
            return;
        path = new Path(points.Prepend((Owner.Position, Owner.Layer)));
        goalType = LocomotorGoal.PositionOnPath;
        desiredSpeed = FastAsPossible;
        CurrentLocomotor.StartMove();
        Wake();
    }

    /// <summary>Distance-to-goal along a flight path instead of the ground path (aircraft, hovercraft).</summary>
    protected virtual bool TreatAsAircraftForLocoDistToGoal =>
        !IsDoingGroundMovement || CurrentLocomotor?.Appearance == Appearance.Hover;

    /// <summary>
    /// Derived AIs with their own states (jets taxiing, taking off, landing...) run them here.
    /// Return true to skip the normal order handling this frame; locomotion still happens.
    /// </summary>
    protected virtual bool UpdateDerivedState() => false;

    // The path was given point by point (taxiing), not found by the pathfinder: never re-plan it.
    bool directPath;

    /// <summary>Follow exactly these points (no pathfinding), e.g. a jet's taxi route.</summary>
    /// <summary>Adds a point to the end of the path being followed.</summary>
    protected void AppendPathNode(Vector3 point)
    {
        if (path == null)
            return;
        path.Append(point);
        Destination = point;
    }

    protected void SetDirectPath(IReadOnlyList<Vector3> points, Vector3? from = null)
    {
        ClearPath();
        if (points.Count == 0 || CurrentLocomotor == null)
            return;
        path = new Path(points.Prepend(from ?? Owner.Position));
        directPath = true;
        Destination = points[^1];
        goalType = LocomotorGoal.PositionOnPath;
        desiredSpeed = FastAsPossible;
        CurrentLocomotor.StartMove();
        Wake();
    }

    /// <summary>Plan a path to a point without changing the AI state (internal move states).</summary>
    protected void MoveInternal(Vector3 destination) => BeginMove(destination, keepState: true);

    protected bool HasPath => path != null;

    /// <summary>Points appended to the current path (a jet's improved parking spot).</summary>
    protected void AppendToPath(Vector3 point) => path?.Append(point);

    /// <summary>
    /// A newly built unit leaving its factory along a set route (an airfield's hangar to its
    /// parking spot). Jets taxi instead.
    /// </summary>
    public virtual void FollowExitProductionPath(IReadOnlyList<Vector3> points, GameObject producer, CommandSource source = CommandSource.AI)
    {
        IgnoreObstacle(producer);
        FollowWaypoints(points, source);
    }

    /// <summary>The object being attacked has died (jets head home soon after).</summary>
    protected virtual void OnVictimDead() { }

    void ClearPath()
    {
        path = null;
        directPath = false;
        if (goalType == LocomotorGoal.PositionOnPath)
            goalType = LocomotorGoal.None;
    }

    protected void SetGoalAngle(float angle)
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
    protected virtual void Decelerate()
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
    public float LocomotorDistanceToGoal()
    {
        if (path == null || path.IsEmpty || CurrentLocomotor == null)
            return 0;
        var last = path.LastNode;
        if (CurrentLocomotor.IsCloseEnoughDist3D || Owner.IsKindOf("PROJECTILE"))
            return Vector3.Distance(Owner.Position, last);
        bool treatAsAircraft = TreatAsAircraftForLocoDistToGoal;
        float dist = treatAsAircraft ? path.ComputeFlightDistToGoal(Owner.Position).Distance
            : path.ComputePointOnPath(Owner.Position, Game.Pathfinder, Surfaces, Owner.Layer).DistAlongPath;
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
        if (directPath && (isBlockedAndStuck || blockedFrames > 2 * LogicTime.FramesPerSecond))
        {
            isBlockedAndStuck = false;
            blockedFrames = 0;
        }
        else if ((isBlockedAndStuck || blockedFrames > 2 * LogicTime.FramesPerSecond) && Frame >= queuedPathFrame)
        {
            if (pathTimestamp > Frame - 3)
            {
                // Re-planning again so soon can spin: wait a second first, and let a stuck unit
                // push through for a while (the original's requestPath).
                queuedPathFrame = Frame + LogicTime.FramesPerSecond;
                if (isBlockedAndStuck)
                {
                    SetIgnoreCollisionTime(2 * LogicTime.FramesPerSecond);
                    blockedFrames = 0;
                    isBlocked = false;
                    isBlockedAndStuck = false;
                }
            }
            else
            {
                isBlockedAndStuck = false;
                BeginMove(Destination, keepState: true);
                if (path == null)
                    return true;
            }
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

    Vector3 tdProgressPosition;
    int tdProgressFrame;

    /// <summary>
    /// OpenGZH addition for Tiberian Dawn units (KindOf TD_OBJECT): Zero Hour's bumping rules can leave two
    /// units that meet head-on both waiting at zero speed for good (each is blocked by the other, which is
    /// "moving"). TD units get past each other (DriveClass waits, then shuffles round), so a TD unit that
    /// has made no headway along its path for three seconds ignores collisions for two seconds.
    /// </summary>
    void BreakTdDeadlock()
    {
        if (goalType != LocomotorGoal.PositionOnPath || !Owner.IsKindOf("TD_OBJECT"))
        {
            tdProgressFrame = Frame;
            tdProgressPosition = Owner.Position;
            return;
        }
        if (MathUtil.DistanceSquared2D(Owner.Position, tdProgressPosition) > 4 * 4)
        {
            tdProgressFrame = Frame;
            tdProgressPosition = Owner.Position;
        }
        else if (Frame - tdProgressFrame > 3 * LogicTime.FramesPerSecond)
        {
            SetIgnoreCollisionTime(2 * LogicTime.FramesPerSecond);
            tdProgressFrame = Frame;
        }
    }

    /// <summary>Applies this frame's locomotor goal (the original's AIUpdateInterface::doLocomotor).</summary>
    void DoLocomotor()
    {
        if (Owner.IsKindOf(Kind.Immobile))
            return;
        ChooseGoodLocomotorFromCurrentSet();
        BreakTdDeadlock();
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
                        (goalPos, onPathDistToGoal) = path.ComputePointOnPath(Owner.Position, Game.Pathfinder, Surfaces, Owner.Layer);
                        Game.Pathfinder.UpdateLayer(Owner, path.CurrentLayer);
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
                // A waypoint mode carries on by itself (it knows which waypoint is next).
                if (scriptMode == ScriptMode.None)
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

public enum AIOrderType { Stop, MoveTo, AttackMoveTo, FollowWaypoints, AttackObject, AttackPosition, Enter, ExitContainer, Face, Guard, Evacuate, MoveToAndEvacuate, MoveToAndEvacuateAndExit, CombatDrop, RappelInto, Dock }

/// <summary>An order on its way into an AI (see <see cref="AIUpdateInterface.OnOrder"/>).</summary>
public readonly record struct AIOrder(AIOrderType Type, CommandSource Source, System.Numerics.Vector3? Position, uint TargetId = 0);
