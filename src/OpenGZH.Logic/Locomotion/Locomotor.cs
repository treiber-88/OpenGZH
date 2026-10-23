using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Locomotion;

/// <summary>Which way a locomotor is turning its object this frame (physics uses it for banking).</summary>
public enum Turning { None, Positive, Negative }

/// <summary>
/// One unit's working copy of a locomotor (the original "Locomotor"): it moves its object by
/// applying forces to the object's PhysicsBehavior, in the way its Appearance dictates (legs,
/// wheels, treads, hover, wings, thrust...), and keeps it at the right height (ZAxisBehavior).
/// This is a line-by-line port of the original, so units accelerate, brake, turn and fly as they did.
/// </summary>
public sealed class Locomotor
{
    const float DonutTimeDelaySeconds = 2.5f;
    const float DonutDistance = 4 * PathCell;
    const float MaxBrakingFactor = 5.0f;
    const float PathCell = Formats.Maps.MapFile.CellSize;

    [Flags]
    enum LocoFlag
    {
        None = 0,
        IsBraking = 1 << 0,
        AllowInvalidPosition = 1 << 1,
        MaintainPosIsValid = 1 << 2,
        PreciseZPos = 1 << 3,
        NoSlowDownAsApproachingDest = 1 << 4,
        OverWater = 1 << 5,
        UltraAccurate = 1 << 6,
        MovingBackwards = 1 << 7,
        DoingThreePointTurn = 1 << 8,
        Climbing = 1 << 9,
        IsCloseEnoughDist3D = 1 << 10,
        OffsetIncreasing = 1 << 11,
    }

    public LocomotorTemplate Template { get; }
    readonly GameLogic game;
    LocoFlag flags;
    float brakingFactor = 1;
    float maxLift = LocomotorTemplate.BigNumber;
    float maxSpeed = LocomotorTemplate.BigNumber;
    float maxAccel = LocomotorTemplate.BigNumber;
    float maxBraking = LocomotorTemplate.BigNumber;
    float maxTurnRate = LocomotorTemplate.BigNumber;
    float angleOffset;
    float offsetIncrement;
    int donutTimer;
    Vector3 maintainPos;

    public float CloseEnoughDistance { get; set; }
    public float PreferredHeight { get; set; }
    public float PreferredHeightDamping { get; set; }

    public Locomotor(LocomotorTemplate template, GameLogic game)
    {
        Template = template;
        this.game = game;
        CloseEnoughDistance = template.CloseEnoughDistance;
        Set(LocoFlag.IsCloseEnoughDist3D, template.CloseEnoughDist3D);
        PreferredHeight = template.PreferredHeight;
        PreferredHeightDamping = template.PreferredHeightDamping;
        angleOffset = game.Random.Float(-MathF.PI / 6, MathF.PI / 6);
        offsetIncrement = MathF.PI / 40 * (game.Random.Float(0.8f, 1.2f) / template.WanderLengthFactor);
        Set(LocoFlag.OffsetIncreasing, game.Random.Int(0, 1) == 1);
        donutTimer = game.Frame + (int)(DonutTimeDelaySeconds * LogicTime.FramesPerSecond);
    }

    bool Get(LocoFlag flag) => (flags & flag) != 0;
    void Set(LocoFlag flag, bool on) => flags = on ? flags | flag : flags & ~flag;

    // ---- Settings other code changes ----

    public Surfaces Surfaces => Template.Surfaces;
    public Appearance Appearance => Template.Appearance;
    public float MinSpeed => Template.MinSpeed;
    public bool IsCloseEnoughDist3D { get => Get(LocoFlag.IsCloseEnoughDist3D); set => Set(LocoFlag.IsCloseEnoughDist3D, value); }
    public bool UltraAccurate { get => Get(LocoFlag.UltraAccurate); set => Set(LocoFlag.UltraAccurate, value); }
    public bool UsePreciseZPos { get => Get(LocoFlag.PreciseZPos); set => Set(LocoFlag.PreciseZPos, value); }
    public bool NoSlowDownAsApproachingDest { get => Get(LocoFlag.NoSlowDownAsApproachingDest); set => Set(LocoFlag.NoSlowDownAsApproachingDest, value); }
    public bool AllowInvalidPosition { get => Get(LocoFlag.AllowInvalidPosition); set => Set(LocoFlag.AllowInvalidPosition, value); }
    public bool IsMovingBackwards => Get(LocoFlag.MovingBackwards);
    public void SetMaxLift(float lift) => maxLift = lift;
    public void SetMaxSpeed(float speed) => maxSpeed = speed;
    public void SetMaxAcceleration(float accel) => maxAccel = accel;
    public void SetMaxBraking(float braking) => maxBraking = braking;
    public void SetMaxTurnRate(float turn) => maxTurnRate = turn;
    public void RestorePreferredHeightFromTemplate() => PreferredHeight = Template.PreferredHeight;

    public void StartMove() => donutTimer = game.Frame + (int)(DonutTimeDelaySeconds * LogicTime.FramesPerSecond);

    // ---- Limits by damage ----

    static bool Penalized(GameObject obj) =>
        (obj.Body?.DamageState ?? BodyDamageState.Pristine) >= BodyDamageState.ReallyDamaged; // MovementPenaltyDamageState

    public float MaxSpeedFor(GameObject obj) => Math.Min(Penalized(obj) ? Template.SpeedDamaged : Template.Speed, maxSpeed);

    public float MaxTurnRateFor(GameObject obj)
    {
        float turn = Math.Min(Penalized(obj) ? Template.TurnRateDamaged : Template.TurnRate, maxTurnRate);
        return UltraAccurate ? turn * 2 : turn; // monster turning ability
    }

    public float MaxAccelerationFor(GameObject obj) => Math.Min(Penalized(obj) ? Template.AccelerationDamaged : Template.Acceleration, maxAccel);
    public float Braking => Math.Min(Template.Braking, maxBraking);
    public float MaxLiftFor(GameObject obj) => Math.Min(Penalized(obj) ? Template.LiftDamaged : Template.Lift, maxLift);

    public float MinTurnRadius(GameObject obj, out float timeToTravel)
    {
        float turnRate = MaxTurnRateFor(obj);
        float radius = turnRate > 0 ? MinSpeed / turnRate : LocomotorTemplate.BigNumber;
        timeToTravel = MinSpeed > 0 ? radius / MinSpeed : 0;
        return radius;
    }

    // ---- Helpers ----

    static float SlowDownDistance(float curSpeed, float desiredSpeed, float maxBraking)
    {
        float delta = curSpeed - desiredSpeed;
        if (delta <= 0)
            return 0;
        const float Fudge = 1.05f;
        return delta * delta / MathF.Abs(maxBraking) * 0.5f * Fudge;
    }

    static bool IsNearlyZero(float a) => MathF.Abs(a) < 0.001f;

    static float AngleDiff(float a, float b) => MathUtil.NormalizeAngle(a - b);

    /// <summary>The object's facing as a 3D unit vector (its "X vector").</summary>
    static Vector3 ForwardVector(GameObject obj)
    {
        float cp = MathF.Cos(obj.Pitch);
        return new Vector3(MathF.Cos(obj.Orientation) * cp, MathF.Sin(obj.Orientation) * cp, MathF.Sin(obj.Pitch));
    }

    /// <summary>Rotates <paramref name="current"/> towards <paramref name="goal"/> by at most maxAngle (negative = that fraction of the angle). Returns the angle turned.</summary>
    static float TryToRotateVector3D(float maxAngle, Vector3 current, Vector3 goal, out Vector3 actual)
    {
        if (IsNearlyZero(maxAngle))
        {
            actual = current;
            return 0;
        }
        var cur = Vector3.Normalize(current);
        var target = Vector3.Normalize(goal);
        float angleBetween = MathF.Acos(Math.Clamp(Vector3.Dot(cur, target), -1, 1));
        if (maxAngle < 0)
        {
            maxAngle = -maxAngle * angleBetween;
            if (IsNearlyZero(maxAngle))
            {
                actual = current;
                return 0;
            }
        }
        if (MathF.Abs(angleBetween) <= maxAngle)
        {
            actual = target;
            return angleBetween;
        }
        var axis = Vector3.Cross(cur, target);
        if (axis.LengthSquared() < 1e-12f)
        {
            actual = cur;
            return 0;
        }
        actual = Vector3.Transform(cur, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), maxAngle));
        return maxAngle;
    }

    /// <summary>Turns the object (yaw and pitch) towards a 3D direction by at most maxTurnRate.</summary>
    static float TryToOrientInThisDirection3D(GameObject obj, float maxTurnRate, Vector3 desired)
    {
        float relAngle = TryToRotateVector3D(maxTurnRate, ForwardVector(obj), desired, out var actual);
        if (relAngle != 0)
        {
            obj.SetOrientation(MathF.Atan2(actual.Y, actual.X));
            obj.Pitch = MathF.Asin(Math.Clamp(actual.Z, -1, 1));
            obj.Roll = 0;
        }
        return relAngle;
    }

    /// <summary>
    /// For thrust: the direction to push in so the velocity bends towards the goal fastest,
    /// taking the current velocity (and gravity) into account.
    /// </summary>
    Vector3 DirectionToApplyThrust(GameObject obj, PhysicsBehavior physics, Vector3 goalPos, float accel)
    {
        var toGoal = goalPos - obj.Position;
        if (IsNearlyZero(toGoal.LengthSquared()))
            return ForwardVector(obj);
        var curVel = physics.Velocity;
        curVel.Z += game.Catalog.GameData.Gravity;
        float distToGoal = toGoal.Length();
        float curVelMagSqr = curVel.LengthSquared();
        float curVelMag = MathF.Sqrt(curVelMagSqr);
        float denom = curVelMagSqr - accel * accel;
        if (!IsNearlyZero(denom))
        {
            float t = distToGoal * (curVelMag + accel) / denom;
            float t2 = distToGoal * (curVelMag - accel) / denom;
            if (t >= 0 || t2 >= 0)
            {
                if (t < 0 || t2 >= 0 && t2 < t)
                    t = t2;
                if (!IsNearlyZero(t))
                {
                    var dir = toGoal / t - curVel;
                    if (dir.LengthSquared() > 0)
                        return Vector3.Normalize(dir);
                }
            }
        }
        return Vector3.Normalize(toGoal);
    }

    float SurfaceHeightAt(float x, float y) =>
        game.Terrain.IsUnderwater(x, y, out float waterZ) ? waterZ : game.Terrain.GroundHeight(x, y);

    // ---- Turning ----

    public Turning RotateTowardsPosition(GameObject obj, Vector3 goal) => RotateTowardsPosition(obj, goal, out _);

    public Turning RotateTowardsPosition(GameObject obj, Vector3 goal, out float relAngle) =>
        RotateAroundPivot(obj, goal, MaxTurnRateFor(obj), out relAngle);

    /// <summary>Turns around the TurnPivotOffset point (e.g. a tank's rear) instead of the centre.</summary>
    Turning RotateAroundPivot(GameObject obj, Vector3 goal, float turnRate, out float relAngle)
    {
        relAngle = 0;
        float angle = obj.Orientation;
        float offset = Get(LocoFlag.IsBraking) ? 0 : Template.TurnPivotOffset;
        Turning turn;
        if (offset != 0)
        {
            float pivotOffset = offset * obj.BoundingCircleRadius;
            var dir = MathUtil.Forward(angle);
            var turnPos = obj.Position + dir * pivotOffset;
            float dx = goal.X - turnPos.X, dy = goal.Y - turnPos.Y;
            if (MathF.Abs(dx) < 0.1f && MathF.Abs(dy) < 0.1f)
                return Turning.None;
            float amount = AngleDiff(MathF.Atan2(dy, dx), angle);
            relAngle = amount;
            (amount, turn) = Clamp(amount, turnRate);
            // Rotate the object about the pivot point.
            var rel = obj.Position - turnPos;
            float c = MathF.Cos(amount), s = MathF.Sin(amount);
            var rotated = new Vector3(rel.X * c - rel.Y * s, rel.X * s + rel.Y * c, rel.Z);
            obj.SetPosition(new Vector3(turnPos.X + rotated.X, turnPos.Y + rotated.Y, obj.Position.Z));
            obj.SetOrientation(MathUtil.NormalizeAngle(angle + amount));
        }
        else
        {
            float amount = AngleDiff(MathF.Atan2(goal.Y - obj.Position.Y, goal.X - obj.Position.X), angle);
            relAngle = amount;
            (amount, turn) = Clamp(amount, turnRate);
            obj.SetOrientation(MathUtil.NormalizeAngle(angle + amount));
        }
        return turn;

        static (float, Turning) Clamp(float amount, float max) =>
            amount > max ? (max, Turning.Positive) : amount < -max ? (-max, Turning.Negative) : (amount, Turning.None);
    }

    // ---- Moving ----

    /// <summary>Copies the locomotor's friction and ground-sticking options onto the physics.</summary>
    public void SetPhysicsOptions(GameObject obj)
    {
        if (obj.FindModule<PhysicsBehavior>() is not { } physics)
            return;
        const float ExtraFriction = 0.5f;
        physics.ExtraFriction = Template.Extra2DFriction + (UltraAccurate ? ExtraFriction : 0);
        physics.AllowAirborneFriction = Template.Apply2DFrictionWhenAirborne;
        physics.StickToGround = Template.StickToGround;
    }

    public void MoveTowardsAngle(GameObject obj, float goalAngle)
    {
        Set(LocoFlag.MaintainPosIsValid, false);
        if (obj.FindModule<PhysicsBehavior>() is not { } physics || physics.IsStunned)
            return;
        if (MinSpeed > 0)
        {
            var desired = obj.Position + new Vector3(MathF.Cos(goalAngle), MathF.Sin(goalAngle), 0) * MinSpeed * 2;
            bool blocked = false;
            MoveTowardsPosition(obj, desired, 99999f, MinSpeed, ref blocked);
            return;
        }
        var far = obj.Position + new Vector3(MathF.Cos(goalAngle), MathF.Sin(goalAngle), 0) * 1000;
        physics.Turning = RotateTowardsPosition(obj, far);
        HandleBehaviorZ(obj, physics, obj.Position);
    }

    /// <summary>
    /// Moves one frame towards <paramref name="goalPos"/>: <paramref name="onPathDistToGoal"/> is how
    /// far the path still goes (so the unit brakes in time); <paramref name="blocked"/> says another
    /// unit is in the way (and comes back false once the locomotor has dealt with that).
    /// </summary>
    public void MoveTowardsPosition(GameObject obj, Vector3 goalPos, float onPathDistToGoal, float desiredSpeed, ref bool blocked)
    {
        Set(LocoFlag.MaintainPosIsValid, false);
        float max = MaxSpeedFor(obj);
        if (desiredSpeed > max)
            desiredSpeed = max;
        float distToStopAtMaxSpeed = max / Braking * max / 2;
        if (onPathDistToGoal > PathCell && onPathDistToGoal > distToStopAtMaxSpeed)
        {
            Set(LocoFlag.IsBraking, false);
            brakingFactor = 1;
        }

        if (obj.FindModule<PhysicsBehavior>() is not { } physics || physics.IsStunned)
            return;

        if ((Template.Surfaces & Surfaces.Air) == 0 && !game.Pathfinder.IsValidMovementTerrain(obj.Position, Template.Surfaces, obj.Layer) && !AllowInvalidPosition)
        {
            if (FixInvalidPosition(obj, physics))
                return;
        }

        float dx = goalPos.X - obj.Position.X, dy = goalPos.Y - obj.Position.Y, dz = goalPos.Z - obj.Position.Z;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist > onPathDistToGoal)
        {
            if (!obj.IsKindOf("PROJECTILE") && dist > 2 * onPathDistToGoal)
                Set(LocoFlag.IsBraking, true);
            onPathDistToGoal = dist;
        }

        float heightAboveSurface = obj.Position.Z - game.Terrain.LayerHeight(obj.Position.X, obj.Position.Y, obj.Layer);
        bool treatAsAirborne = heightAboveSurface > -(3 * 3) * game.Catalog.GameData.Gravity;

        physics.ApplyMotiveForce(Vector3.Zero);

        if (blocked)
        {
            if (desiredSpeed > physics.VelocityMagnitude)
                blocked = false;
            if (treatAsAirborne && (Template.Surfaces & Surfaces.Air) != 0)
                blocked = false;
        }
        if (blocked)
        {
            physics.ScrubVelocity2D(desiredSpeed); // stop before running into whatever blocks us
            if (Template.WanderWidthFactor == 0)
                blocked = RotateAroundPivot(obj, goalPos, MaxTurnRateFor(obj), out _) != Turning.None;
            HandleBehaviorZ(obj, physics, goalPos);
            return;
        }

        if (Template.Appearance == Appearance.Wings)
            Set(LocoFlag.IsBraking, false);

        bool wasBraking = obj.HasStatus(Flags.ObjectStatus.IndexOf("IS_BRAKING"));
        physics.Turning = Turning.None;
        if (Template.AllowAirborneMotiveForce || !treatAsAirborne)
        {
            switch (Template.Appearance)
            {
                case Appearance.TwoLegs: MoveTowardsPositionLegs(obj, physics, goalPos, onPathDistToGoal, desiredSpeed); break;
                case Appearance.Climber: MoveTowardsPositionClimb(obj, physics, goalPos, onPathDistToGoal, desiredSpeed); break;
                case Appearance.FourWheels:
                case Appearance.Motorcycle: MoveTowardsPositionWheels(obj, physics, goalPos, onPathDistToGoal, desiredSpeed); break;
                case Appearance.Treads: MoveTowardsPositionTreads(obj, physics, goalPos, onPathDistToGoal, desiredSpeed); break;
                case Appearance.Hover: MoveTowardsPositionHover(obj, physics, goalPos, onPathDistToGoal, desiredSpeed); break;
                case Appearance.Wings: MoveTowardsPositionOther(obj, physics, goalPos, onPathDistToGoal, desiredSpeed); break;
                case Appearance.Thrust: MoveTowardsPositionThrust(obj, physics, goalPos, onPathDistToGoal, desiredSpeed); break;
                default: MoveTowardsPositionOther(obj, physics, goalPos, onPathDistToGoal, desiredSpeed); break;
            }
        }
        HandleBehaviorZ(obj, physics, goalPos);
        obj.SetStatus("IS_BRAKING", Get(LocoFlag.IsBraking));

        if (wasBraking)
        {
            // Braking units slide the last bit of the way into place.
            const float MinVelocity = PathCell / LogicTime.FramesPerSecond;
            var pos = obj.Position;
            if (obj.IsKindOf("PROJECTILE"))
            {
                obj.SetStatus("IS_BRAKING");
                dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                float vel = Math.Clamp(physics.VelocityMagnitude, MinVelocity, Math.Max(MinVelocity, dist));
                if (vel > dist) vel = dist;
                if (dist > 0.001f)
                    pos += new Vector3(dx, dy, dz) / dist * vel;
            }
            else if (dist > 0.001f)
            {
                float vel = MathF.Abs(physics.ForwardSpeed2D);
                if (vel < MinVelocity) vel = MinVelocity;
                if (vel > dist) vel = dist;
                pos += new Vector3(dx / dist * vel, dy / dist * vel, 0);
            }
            obj.SetPosition(pos);
        }
    }

    /// <summary>Applies a forward/backward force to reach goalSpeed, never more than needed.</summary>
    static void ApplySpeedForce(GameObject obj, PhysicsBehavior physics, float speedDelta, float acceleration)
    {
        float mass = physics.Mass;
        float accelForce = mass * acceleration;
        float maxForceNeeded = mass * speedDelta;
        if (MathF.Abs(accelForce) > MathF.Abs(maxForceNeeded))
            accelForce = maxForceNeeded;
        var dir = MathUtil.Forward(obj.Orientation);
        physics.ApplyMotiveForce(new Vector3(accelForce * dir.X, accelForce * dir.Y, 0));
    }

    /// <summary>The braking state machine shared by treads and wheels.</summary>
    float BrakingGoalSpeed(float goalSpeed, float actualSpeed, float slowDownDist, float onPathDistToGoal, bool clampFactorToOne)
    {
        if (!Get(LocoFlag.IsBraking))
            return goalSpeed;
        brakingFactor = slowDownDist / onPathDistToGoal;
        brakingFactor *= brakingFactor;
        if (brakingFactor > MaxBrakingFactor)
            brakingFactor = MaxBrakingFactor;
        if (clampFactorToOne)
            brakingFactor = 1; // wheels: the factor is computed, then reset (as in the original)
        if (slowDownDist > onPathDistToGoal)
            return Math.Max(0, actualSpeed - Braking);
        if (slowDownDist > onPathDistToGoal * 0.75f)
            return Math.Max(0, actualSpeed - Braking / 2);
        return actualSpeed;
    }

    void MoveTowardsPositionTreads(GameObject obj, PhysicsBehavior physics, Vector3 goalPos, float onPathDistToGoal, float desiredSpeed)
    {
        float max = MaxSpeedFor(obj);
        if (desiredSpeed > max)
            desiredSpeed = max;
        float maxAcceleration = MaxAccelerationFor(obj);
        physics.Turning = RotateTowardsPosition(obj, goalPos, out float relAngle);
        float angleCoeff = Math.Min(1, MathF.Abs(relAngle) / (MathF.PI / 4));
        float dx = obj.Position.X - goalPos.X, dy = obj.Position.Y - goalPos.Y;
        float goalSpeed = (1 - angleCoeff) * desiredSpeed;
        float actualSpeed = physics.ForwardSpeed2D;
        float slowDownTime = actualSpeed / Braking;
        float slowDownDist = actualSpeed / 1.50f * slowDownTime;
        if (dx * dx + dy * dy < 4 * PathCell * PathCell && angleCoeff > 0.05f)
            goalSpeed = actualSpeed * 0.6f;
        if (onPathDistToGoal < slowDownDist && !Get(LocoFlag.IsBraking) && !NoSlowDownAsApproachingDest)
        {
            Set(LocoFlag.IsBraking, true);
            brakingFactor = 1.1f;
        }
        if (onPathDistToGoal > PathCell && onPathDistToGoal > 2 * slowDownDist)
            Set(LocoFlag.IsBraking, false);
        goalSpeed = BrakingGoalSpeed(goalSpeed, actualSpeed, slowDownDist, onPathDistToGoal, clampFactorToOne: false);

        float speedDelta = goalSpeed - actualSpeed;
        if (speedDelta != 0)
            ApplySpeedForce(obj, physics, speedDelta, speedDelta > 0 ? maxAcceleration : -brakingFactor * Braking);
    }

    void MoveTowardsPositionWheels(GameObject obj, PhysicsBehavior physics, Vector3 goalPos, float onPathDistToGoal, float desiredSpeed)
    {
        float max = MaxSpeedFor(obj);
        float turnRateMax = MaxTurnRateFor(obj);
        float maxAcceleration = MaxAccelerationFor(obj);
        if (desiredSpeed > max)
            desiredSpeed = max;
        float turnSpeed = Template.MinTurnSpeed;
        float angle = obj.Orientation;
        float desiredAngle = MathF.Atan2(goalPos.Y - obj.Position.Y, goalPos.X - obj.Position.X);
        float relAngle = AngleDiff(desiredAngle, angle);
        bool moveBackwards = false;
        if (turnSpeed < max / 4)
            turnSpeed = max / 4;
        float actualSpeed = physics.ForwardSpeed2D;
        bool threePointTurn = false;

        if (actualSpeed == 0)
        {
            Set(LocoFlag.MovingBackwards, false);
            if (Template.CanMoveBackwards && MathF.Abs(relAngle) > MathF.PI / 2)
            {
                Set(LocoFlag.MovingBackwards, true);
                Set(LocoFlag.DoingThreePointTurn, onPathDistToGoal > 5 * obj.Geometry.MajorRadius);
            }
        }
        if (Get(LocoFlag.MovingBackwards))
        {
            if (MathF.Abs(relAngle) < MathF.PI / 2)
            {
                moveBackwards = false;
                Set(LocoFlag.MovingBackwards, false);
            }
            else
            {
                moveBackwards = true;
                Set(LocoFlag.DoingThreePointTurn, onPathDistToGoal > 5 * obj.Geometry.MajorRadius);
                threePointTurn = Get(LocoFlag.DoingThreePointTurn);
                if (!threePointTurn)
                {
                    desiredAngle = AngleDiff(desiredAngle, MathF.PI);
                    relAngle = AngleDiff(desiredAngle, angle);
                }
            }
        }

        const float SmallTurn = MathF.PI / 20;
        if (MathF.Abs(relAngle) > SmallTurn && desiredSpeed > turnSpeed)
            desiredSpeed = turnSpeed;
        float goalSpeed = desiredSpeed;
        if (moveBackwards)
            actualSpeed = -actualSpeed;
        float slowDownTime = actualSpeed / Braking + 1;
        float slowDownDist = actualSpeed / 1.5f * slowDownTime + actualSpeed;
        float effectiveSlowDownDist = Math.Max(slowDownDist, PathCell);

        const float FifteenDegrees = MathF.PI / 12;
        const float ProjectFrames = LogicTime.FramesPerSecond / 2f;
        if (MathF.Abs(relAngle) > FifteenDegrees)
        {
            // Look half a second ahead along the turn: if that would leave valid ground, turn on the spot instead.
            float distance = ProjectFrames * (goalSpeed + actualSpeed) / 2;
            float turnFactor = Math.Min(1, (goalSpeed + actualSpeed) / 2 / turnSpeed);
            float turnAmount = ProjectFrames * turnFactor * turnRateMax / 4;
            float targetAngle = relAngle < 0 ? obj.Orientation - turnAmount : obj.Orientation + turnAmount;
            var offset = new Vector3(MathF.Cos(targetAngle) * distance, MathF.Sin(targetAngle) * distance, 0);
            var nextPos = obj.Position + offset;
            var halfPos = obj.Position + offset / 2;
            if (!game.Pathfinder.IsValidMovementTerrain(halfPos, Template.Surfaces, obj.Layer) || !game.Pathfinder.IsValidMovementTerrain(nextPos, Template.Surfaces, obj.Layer))
            {
                physics.Turning = RotateTowardsPosition(obj, goalPos);
                physics.ApplyMotiveForce(Vector3.Zero);
                return;
            }
        }

        if (onPathDistToGoal < effectiveSlowDownDist && !Get(LocoFlag.IsBraking) && !NoSlowDownAsApproachingDest)
        {
            Set(LocoFlag.IsBraking, true);
            brakingFactor = 1.1f;
        }
        if (onPathDistToGoal > PathCell && onPathDistToGoal > 2 * slowDownDist)
            Set(LocoFlag.IsBraking, false);
        if (onPathDistToGoal > DonutDistance)
            donutTimer = game.Frame + (int)(DonutTimeDelaySeconds * LogicTime.FramesPerSecond);
        else if (donutTimer < game.Frame)
            Set(LocoFlag.IsBraking, true); // stop circling round a point it can't quite reach
        goalSpeed = BrakingGoalSpeed(goalSpeed, actualSpeed, slowDownDist, onPathDistToGoal, clampFactorToOne: true);

        float speedFactor = Math.Min(1, MathF.Abs(actualSpeed / turnSpeed));
        float turnLimit = speedFactor * turnRateMax;
        Turning rotating;
        if (moveBackwards && !threePointTurn)
        {
            var backward = obj.Position - (goalPos - obj.Position);
            rotating = RotateAroundPivot(obj, backward with { Z = obj.Position.Z }, turnLimit, out _);
        }
        else
        {
            rotating = RotateAroundPivot(obj, goalPos, turnLimit, out _);
        }
        physics.Turning = rotating;

        float speedDelta = moveBackwards ? -goalSpeed + actualSpeed : goalSpeed - actualSpeed;
        if (speedDelta != 0)
        {
            float acceleration = moveBackwards
                ? (speedDelta < 0 ? -maxAcceleration : brakingFactor * Braking)
                : (speedDelta > 0 ? maxAcceleration : -brakingFactor * Braking);
            ApplySpeedForce(obj, physics, speedDelta, acceleration);
        }
    }

    bool FixInvalidPosition(GameObject obj, PhysicsBehavior physics)
    {
        if (obj.IsKindOf("DOZER"))
            return false;
        int dx = 0, dy = 0;
        for (int j = -1; j < 2; j++)
        {
            for (int i = -1; i < 2; i++)
            {
                var pos = obj.Position + new Vector3(i * PathCell, j * PathCell, 0);
                if (game.Pathfinder.IsValidMovementTerrain(pos, Template.Surfaces, obj.Layer))
                    continue;
                if (i < 0) dx += 1;
                if (i > 0) dx -= 1;
                if (j < 0) dy += 1;
                if (j > 0) dy -= 1;
            }
        }
        if (dx == 0 && dy == 0)
            return false;
        // Push away from the invalid cells, harder if moving into them.
        var correction = new Vector3(dx * physics.Mass / 5, dy * physics.Mass / 5, 0);
        var normalized = Vector3.Normalize(correction);
        var velocity = physics.Velocity;
        float dot = velocity.X * normalized.X + velocity.Y * normalized.Y;
        if (dot > 0.25f)
            return false;
        if (dot < 0)
        {
            dot = MathF.Sqrt(-dot);
            physics.ApplyMotiveForce(new Vector3(normalized.X * dot * physics.Mass, normalized.Y * dot * physics.Mass, 0));
        }
        physics.ApplyMotiveForce(correction);
        return true;
    }

    void MoveTowardsPositionLegs(GameObject obj, PhysicsBehavior physics, Vector3 goalPos, float onPathDistToGoal, float desiredSpeed)
    {
        if (Template.DownhillOnly && obj.Position.Z < goalPos.Z)
            return;
        float maxAcceleration = MaxAccelerationFor(obj);
        float max = MaxSpeedFor(obj);
        if (desiredSpeed > max)
            desiredSpeed = max;
        float actualSpeed = physics.ForwardSpeed2D;
        float angle = obj.Orientation;
        float desiredAngle = MathF.Atan2(goalPos.Y - obj.Position.Y, goalPos.X - obj.Position.X);
        if (Template.WanderWidthFactor != 0)
        {
            // Infantry that wander weave from side to side as they walk.
            float angleLimit = MathF.PI / 8 * Template.WanderWidthFactor;
            if (Get(LocoFlag.OffsetIncreasing))
            {
                angleOffset += offsetIncrement * actualSpeed;
                if (angleOffset > angleLimit)
                    Set(LocoFlag.OffsetIncreasing, false);
            }
            else
            {
                angleOffset -= offsetIncrement * actualSpeed;
                if (angleOffset < -angleLimit)
                    Set(LocoFlag.OffsetIncreasing, true);
            }
            desiredAngle = MathUtil.NormalizeAngle(desiredAngle + angleOffset);
        }
        float relAngle = AngleDiff(desiredAngle, angle);
        MoveTowardsAngle(obj, desiredAngle);
        float angleCoeff = Math.Min(1, MathF.Abs(relAngle) / (MathF.PI / 4));
        float goalSpeed = (1 - angleCoeff) * desiredSpeed;
        float slowDownDist = SlowDownDistance(actualSpeed, Template.MinSpeed, Braking);
        if (onPathDistToGoal < slowDownDist && !NoSlowDownAsApproachingDest)
            goalSpeed = Template.MinSpeed;
        float speedDelta = goalSpeed - actualSpeed;
        if (speedDelta != 0)
            ApplySpeedForce(obj, physics, speedDelta, speedDelta > 0 ? maxAcceleration : -Braking);
    }

    void MoveTowardsPositionClimb(GameObject obj, PhysicsBehavior physics, Vector3 goalPos, float onPathDistToGoal, float desiredSpeed)
    {
        float maxAcceleration = MaxAccelerationFor(obj);
        float max = MaxSpeedFor(obj);
        if (desiredSpeed > max)
            desiredSpeed = max;
        bool moveBackwards = false;
        var pos = obj.Position;
        float dz = pos.Z - goalPos.Z;
        if (dz * dz > PathCell * PathCell)
            Set(LocoFlag.Climbing, true);
        if (MathF.Abs(dz) < 1)
            Set(LocoFlag.Climbing, false);
        if (Get(LocoFlag.Climbing))
        {
            var delta = (goalPos - pos) with { Z = 0 };
            if (delta.LengthSquared() > 0)
                delta = Vector3.Normalize(delta);
            delta += pos with { Z = 0 };
            delta.Z = game.Terrain.GroundHeight(delta.X, delta.Y);
            if (delta.Z < pos.Z - 0.1f)
                moveBackwards = true;
            float groundSlope = Math.Max(1, MathF.Abs(delta.Z - pos.Z));
            if (groundSlope > 1)
                desiredSpeed /= groundSlope * 4;
        }
        Set(LocoFlag.MovingBackwards, moveBackwards);
        float angle = obj.Orientation;
        float desiredAngle = MathF.Atan2(goalPos.Y - pos.Y, goalPos.X - pos.X);
        float relAngle = AngleDiff(desiredAngle, angle);
        if (moveBackwards)
        {
            desiredAngle = AngleDiff(desiredAngle, MathF.PI);
            relAngle = AngleDiff(desiredAngle, angle);
        }
        MoveTowardsAngle(obj, desiredAngle);
        float angleCoeff = Math.Min(1, MathF.Abs(relAngle) / (MathF.PI / 4));
        float goalSpeed = (1 - angleCoeff) * desiredSpeed;
        float actualSpeed = physics.ForwardSpeed2D;
        if (moveBackwards)
            actualSpeed = -actualSpeed;
        float slowDownDist = SlowDownDistance(actualSpeed, Template.MinSpeed, Braking);
        if (onPathDistToGoal < slowDownDist && !NoSlowDownAsApproachingDest)
            goalSpeed = Template.MinSpeed;
        float speedDelta = moveBackwards ? -goalSpeed + actualSpeed : goalSpeed - actualSpeed;
        if (speedDelta != 0)
        {
            float acceleration = moveBackwards
                ? (speedDelta < 0 ? -maxAcceleration : Braking)
                : (speedDelta > 0 ? maxAcceleration : -Braking);
            ApplySpeedForce(obj, physics, speedDelta, acceleration);
        }
    }

    void MoveTowardsPositionHover(GameObject obj, PhysicsBehavior physics, Vector3 goalPos, float onPathDistToGoal, float desiredSpeed)
    {
        MoveTowardsPositionOther(obj, physics, goalPos, onPathDistToGoal, desiredSpeed);
        bool overWater = game.Terrain.IsUnderwater(obj.Position.X, obj.Position.Y, out _);
        if (overWater != Get(LocoFlag.OverWater))
        {
            Set(LocoFlag.OverWater, overWater);
            obj.SetCondition("OVER_WATER", overWater);
        }
    }

    void MoveTowardsPositionThrust(GameObject obj, PhysicsBehavior physics, Vector3 goalPos, float onPathDistToGoal, float desiredSpeed)
    {
        float maxForwardSpeed = MaxSpeedFor(obj);
        desiredSpeed = Math.Clamp(desiredSpeed, Template.MinSpeed, Math.Max(Template.MinSpeed, maxForwardSpeed));
        float actualForwardSpeed = physics.ForwardSpeed3D;
        if (Braking > 0)
        {
            float slowDownDist = SlowDownDistance(actualForwardSpeed, Template.MinSpeed, Braking);
            if (onPathDistToGoal < slowDownDist && !NoSlowDownAsApproachingDest)
                desiredSpeed = Template.MinSpeed;
        }

        var localGoal = goalPos;
        var pos = obj.Position;
        if (PreferredHeight != 0 && !UsePreciseZPos)
        {
            float surface = SurfaceHeightAt(pos.X, pos.Y);
            float delta = (PreferredHeight + surface - pos.Z) * PreferredHeightDamping;
            localGoal.Z = pos.Z + delta;
        }

        var forward = ForwardVector(obj);
        float forwardSpeedDelta = desiredSpeed - actualForwardSpeed;
        float accel = forwardSpeedDelta > 0 || Braking == 0 ? MaxAccelerationFor(obj) : -Braking;
        float turnRate = MaxTurnRateFor(obj);
        var desiredThrustDir = DirectionToApplyThrust(obj, physics, localGoal, accel);
        float maxThrustAngle = turnRate > 0 ? Template.MaxThrustAngle : 0;
        float thrustAngle = TryToRotateVector3D(maxThrustAngle, forward, desiredThrustDir, out var thrustDir);

        if (!IsNearlyZero(physics.VelocityMagnitude))
        {
            var vel = physics.Velocity;
            bool adjust = true;
            if (obj.HasStatus(Flags.ObjectStatus.IndexOf("IS_BRAKING")))
            {
                vel = goalPos - pos;
                if (IsNearlyZero(vel.LengthSquared()))
                    adjust = false;
                turnRate *= 3;
            }
            if (adjust)
                TryToOrientInThisDirection3D(obj, turnRate, vel);
        }

        if (forwardSpeedDelta != 0 || thrustAngle != 0)
        {
            if (maxForwardSpeed <= 0)
                maxForwardSpeed = 0.01f;
            float damping = Math.Clamp(accel / maxForwardSpeed, 0, 1);
            var accelVec = thrustDir * accel - physics.Velocity * damping;
            physics.ApplyMotiveForce(accelVec * physics.Mass);
        }
    }

    /// <summary>The upward push needed to reach the preferred height without overshooting (helicopters, hover).</summary>
    float LiftToUse(GameObject obj, PhysicsBehavior physics, float curZ, float preferredHeight)
    {
        float gravity = game.Catalog.GameData.Gravity;
        float maxGrossLift = MaxLiftFor(obj);
        float maxNetLift = Math.Max(0, maxGrossLift + gravity);
        float curVelZ = physics.Velocity.Z;
        float accelLimit = UltraAccurate
            ? (curVelZ < 0 ? 2 * maxNetLift : -2 * maxNetLift)
            : (curVelZ < 0 ? maxNetLift : gravity);
        float desiredAccel;
        const float TinyAccel = 0.001f;
        if (MathF.Abs(accelLimit) > TinyAccel)
        {
            float deltaZ = preferredHeight - curZ;
            float brakeDist = curVelZ * curVelZ / MathF.Abs(accelLimit);
            if (MathF.Abs(brakeDist) > MathF.Abs(deltaZ))
                desiredAccel = accelLimit;
            else if (MathF.Abs(curVelZ) > Template.SpeedLimitZ)
                desiredAccel = Template.SpeedLimitZ - curVelZ;
            else
                desiredAccel = 2 * (deltaZ - curVelZ);
        }
        else
        {
            desiredAccel = 0;
        }
        float lift = desiredAccel - gravity;
        if (UltraAccurate)
            lift = Math.Clamp(lift, -maxGrossLift, 3 * maxGrossLift);
        else
            lift = Math.Clamp(lift, 0, Math.Max(0, maxGrossLift));
        return lift;
    }

    /// <summary>Keeps the object at its height. Returns whether it must be called every frame.</summary>
    bool HandleBehaviorZ(GameObject obj, PhysicsBehavior physics, Vector3 goalPos)
    {
        switch (Template.ZAxisBehavior)
        {
            case ZAxisBehavior.NoZMotiveForce:
                return false;

            case ZAxisBehavior.SeaLevel:
                if (!obj.IsDisabledBy("DISABLED_HELD"))
                {
                    var pos = obj.Position;
                    pos.Z = game.Terrain.IsUnderwater(pos.X, pos.Y, out float waterZ) ? waterZ : game.Terrain.LayerHeight(pos.X, pos.Y);
                    obj.SetPosition(pos);
                }
                return true;

            case ZAxisBehavior.FixedSurfaceRelativeHeight:
            case ZAxisBehavior.FixedAbsoluteHeight:
            {
                var pos = obj.Position;
                bool surfaceRelative = Template.ZAxisBehavior == ZAxisBehavior.FixedSurfaceRelativeHeight;
                pos.Z = PreferredHeight + (surfaceRelative ? SurfaceHeightAt(pos.X, pos.Y) : 0);
                obj.SetPosition(pos);
                return true;
            }

            case ZAxisBehavior.RelativeToGroundAndBuildings:
            {
                var pos = obj.Position;
                pos.Z = PreferredHeight + game.GroundOrStructureHeight(pos.X, pos.Y);
                obj.SetPosition(pos);
                return true;
            }

            case ZAxisBehavior.RelativeToHighestLayer:
            case ZAxisBehavior.SurfaceRelativeHeight:
            case ZAxisBehavior.AbsoluteHeight:
            {
                if (PreferredHeight == 0 && !UsePreciseZPos)
                    return true;
                var pos = obj.Position;
                float surface = Template.ZAxisBehavior switch
                {
                    ZAxisBehavior.RelativeToHighestLayer => game.Terrain.HighestLayerHeight(pos.X, pos.Y),
                    ZAxisBehavior.SurfaceRelativeHeight => SurfaceHeightAt(pos.X, pos.Y),
                    _ => 0,
                };
                float preferred = UsePreciseZPos ? goalPos.Z : PreferredHeight + surface;
                preferred = pos.Z + (preferred - pos.Z) * PreferredHeightDamping;
                float lift = LiftToUse(obj, physics, pos.Z, preferred);
                if (lift != 0)
                    physics.ApplyMotiveForce(new Vector3(0, 0, lift * physics.Mass));
                return true;
            }
        }
        return true;
    }

    void MoveTowardsPositionOther(GameObject obj, PhysicsBehavior physics, Vector3 goalPos, float onPathDistToGoal, float desiredSpeed)
    {
        float maxAcceleration = MaxAccelerationFor(obj);
        float max = MaxSpeedFor(obj);
        if (desiredSpeed > max)
            desiredSpeed = max;
        float goalSpeed = desiredSpeed;
        float actualSpeed = physics.ForwardSpeed2D;
        var pos = obj.Position;
        var dirToApplyForce = MathUtil.Forward(obj.Orientation);
        if (UltraAccurate
            && MathF.Abs(goalPos.Y - pos.Y) <= goalSpeed * Template.SlideIntoPlaceFactor
            && MathF.Abs(goalPos.X - pos.X) <= goalSpeed * Template.SlideIntoPlaceFactor)
        {
            // Close enough to slide straight into place without turning.
            physics.Turning = Turning.None;
            var slide = (goalPos - pos) with { Z = 0 };
            dirToApplyForce = slide.LengthSquared() > 0 ? Vector3.Normalize(slide) : Vector3.Zero;
        }
        else
        {
            physics.Turning = RotateTowardsPosition(obj, goalPos);
        }
        if (!NoSlowDownAsApproachingDest)
        {
            float slowDownDist = SlowDownDistance(actualSpeed, Template.MinSpeed, Braking);
            if (onPathDistToGoal < slowDownDist)
                goalSpeed = Template.MinSpeed;
        }
        float speedDelta = goalSpeed - actualSpeed;
        if (speedDelta != 0)
        {
            float mass = physics.Mass;
            float accelForce = mass * (speedDelta > 0 ? maxAcceleration : -Braking);
            float maxForceNeeded = mass * speedDelta;
            if (MathF.Abs(accelForce) > MathF.Abs(maxForceNeeded))
                accelForce = maxForceNeeded;
            physics.ApplyMotiveForce(new Vector3(accelForce * dirToApplyForce.X, accelForce * dirToApplyForce.Y, 0));
        }
    }

    // ---- Holding still ----

    /// <summary>Holds the object where it is (planes circle, helicopters hover). Returns whether it must keep being called.</summary>
    public bool MaintainCurrentPosition(GameObject obj)
    {
        if (!Get(LocoFlag.MaintainPosIsValid))
        {
            maintainPos = obj.Position;
            Set(LocoFlag.MaintainPosIsValid, true);
        }
        donutTimer = game.Frame + (int)(DonutTimeDelaySeconds * LogicTime.FramesPerSecond);
        Set(LocoFlag.IsBraking, false);
        if (obj.FindModule<PhysicsBehavior>() is not { } physics)
            return true;

        bool constant;
        switch (Template.Appearance)
        {
            case Appearance.Thrust:
                MoveTowardsPositionThrust(obj, physics, maintainPos, 0, MinSpeed);
                constant = true;
                break;
            case Appearance.TwoLegs:
            case Appearance.Climber:
            case Appearance.FourWheels:
            case Appearance.Motorcycle:
            case Appearance.Treads:
                MaintainOther(physics);
                constant = false;
                break;
            case Appearance.Hover:
                MaintainHover(obj, physics);
                constant = true;
                break;
            case Appearance.Wings:
                MaintainWings(obj, physics);
                constant = true;
                break;
            default:
                MaintainOther(physics);
                constant = true;
                break;
        }
        if (HandleBehaviorZ(obj, physics, maintainPos))
            constant = true;
        return constant;
    }

    void MaintainWings(GameObject obj, PhysicsBehavior physics)
    {
        physics.Turning = Turning.None;
        if (!physics.IsMotive || !obj.IsAboveTerrain)
            return;
        // Circle round the spot.
        float turnRadius = Template.CirclingRadius;
        if (turnRadius == 0)
            turnRadius = MinTurnRadius(obj, out _);
        float dx = maintainPos.X - obj.Position.X, dy = maintainPos.Y - obj.Position.Y;
        float angleToward = IsNearlyZero(dx) && IsNearlyZero(dy) ? obj.Orientation : MathF.Atan2(dy, dx);
        float aimDir = MathF.PI - MathF.PI / 8;
        if (turnRadius < 0)
        {
            turnRadius = -turnRadius;
            aimDir = -aimDir;
        }
        angleToward += aimDir;
        var desired = maintainPos + new Vector3(MathF.Cos(angleToward) * turnRadius, MathF.Sin(angleToward) * turnRadius, 0);
        MoveTowardsPositionOther(obj, physics, desired, 0, Template.MinSpeed);
    }

    void MaintainHover(GameObject obj, PhysicsBehavior physics)
    {
        physics.Turning = Turning.None;
        if (!physics.IsMotive)
            return;
        float actualSpeed = physics.ForwardSpeed2D;
        float minSpeed = Math.Max(1.0e-10f, Template.MinSpeed);
        float speedDelta = minSpeed - actualSpeed;
        if (MathF.Abs(speedDelta) > minSpeed)
            ApplySpeedForce(obj, physics, speedDelta, speedDelta > 0 ? MaxAccelerationFor(obj) : -Braking);
    }

    static void MaintainOther(PhysicsBehavior physics)
    {
        physics.Turning = Turning.None;
        if (physics.IsMotive)
            physics.ScrubVelocity2D(0);
    }

    public override string ToString() => Template.Name;
}
