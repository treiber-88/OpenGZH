using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Locomotion;
using OpenGZH.Logic.Modules.Contain;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// The physics of one object, ported from the original PhysicsBehavior: every frame gravity and
/// friction are applied, forces become acceleration (force / mass), velocity moves the object,
/// and it lands, bounces, takes falling damage or is pushed apart from what it bumps into.
/// Units move because their locomotor applies "motive" forces here; while a motive force is
/// active, forward friction is switched off so the unit keeps its speed.
/// All values are per frame (velocity in world units per frame, acceleration per frame²).
/// </summary>
public sealed class PhysicsBehavior : Module, IUpdateModule, ICreateModule, ICollideModule
{
    const float DefaultLateralFriction = 0.15f;
    const float MinAeroFriction = 0.00f;
    const float MinNonAeroFriction = 0.01f;
    const float MaxFriction = 0.99f;
    const float StunReliefEpsilon = 0.5f;
    const int MotiveFrames = LogicTime.FramesPerSecond / 3;

    // Settings
    float baseMass;
    float shockResistance, shockMaxYaw, shockMaxPitch, shockMaxRoll;
    float forwardFriction, lateralFriction, zFriction, aerodynamicFriction;
    float centerOfMassOffset;
    bool killWhenRestingOnGround;
    float minFallSpeedForDamage, fallHeightDamageFactor, pitchRollYawFactor;
    string crashIntoBuildingWeapon = "", crashIntoNonBuildingWeapon = "";

    // State
    Vector3 accel, prevAccel, velocity;
    float yawRate, pitchRate, rollRate;
    int motiveForceExpires;
    bool originalAllowBounce;
    bool wasAirborneLastFrame, updateEverRun;
    uint currentOverlap, previousOverlap, ignoreCollisionsWith;

    /// <summary>What the object is driving over now and last frame (the client rocks tanks over them).</summary>
    public uint CurrentOverlap => currentOverlap;
    public uint PreviousOverlap => previousOverlap;

    public Turning Turning { get; set; }
    public float ExtraBounciness { get; set; }
    public float ExtraFriction { get; set; }
    public bool AllowBouncing { get; set; }
    public bool AllowCollideForce { get; set; } = true;
    public bool AllowToFall { get; set; }
    public bool StickToGround { get; set; }
    public bool AllowAirborneFriction { get; set; }
    public bool IsInFreeFall { get; set; }
    public bool IsStunned { get; private set; }
    public bool ImmuneToFallingDamage { get; set; }
    public uint LastCollideeId { get; private set; }

    /// <summary>
    /// The object's own module positions it every frame (DumbProjectileBehavior and MissileAIUpdate
    /// fly a computed course), so physics does not integrate its motion; collisions still happen.
    /// </summary>
    public bool MovedByOwnModule { get; set; }

    /// <summary>Mass including anything inside (a loaded transport is heavier).</summary>
    public float Mass
    {
        get
        {
            float mass = baseMass;
            if (Owner.FindModule<ContainModule>() is { } contain)
                foreach (var rider in contain.Passengers)
                    mass += rider.FindModule<PhysicsBehavior>()?.Mass ?? 0;
            return mass;
        }
        set => baseMass = value;
    }

    public Vector3 Velocity { get => velocity; set => velocity = value; }
    public Vector3 Acceleration => accel;
    public Vector3 PreviousAcceleration => prevAccel;
    public float VelocityMagnitude => velocity.Length();
    public float CenterOfMassOffset => centerOfMassOffset;
    public float YawRate { get => yawRate; set => yawRate = value; }
    public float PitchRate { get => pitchRate; set => pitchRate = value; }
    public float RollRate { get => rollRate; set => rollRate = value; }
    bool HasPitchRollYaw => pitchRate != 0 || rollRate != 0 || yawRate != 0;

    /// <summary>A locomotor pushed the object within the last third of a second.</summary>
    public bool IsMotive => motiveForceExpires > Frame;

    static float HeightToSpeed(float height, float gravity) => MathF.Sqrt(MathF.Abs(2 * gravity * height));

    protected override void Initialize()
    {
        float gravity = Game.Catalog.GameData.Gravity;
        baseMass = FloatSetting("Mass", 1);
        shockResistance = FloatSetting("ShockResistance");
        shockMaxYaw = FloatSetting("ShockMaxYaw", 0.05f);
        shockMaxPitch = FloatSetting("ShockMaxPitch", 0.025f);
        shockMaxRoll = FloatSetting("ShockMaxRoll", 0.025f);
        // Frictions are given per second and used per frame.
        forwardFriction = Definition.Settings.Has("ForwardFriction") ? FloatSetting("ForwardFriction") * LogicTime.SecondsPerFrame : 0.15f;
        lateralFriction = Definition.Settings.Has("LateralFriction") ? FloatSetting("LateralFriction") * LogicTime.SecondsPerFrame : 0.15f;
        zFriction = Definition.Settings.Has("ZFriction") ? FloatSetting("ZFriction") * LogicTime.SecondsPerFrame : 0.8f;
        aerodynamicFriction = Definition.Settings.Has("AerodynamicFriction") ? FloatSetting("AerodynamicFriction") * LogicTime.SecondsPerFrame : 0;
        centerOfMassOffset = FloatSetting("CenterOfMassOffset");
        AllowBouncing = originalAllowBounce = BoolSetting("AllowBouncing");
        AllowCollideForce = BoolSetting("AllowCollideForce", true);
        killWhenRestingOnGround = BoolSetting("KillWhenRestingOnGround");
        minFallSpeedForDamage = HeightToSpeed(Definition.Settings.Has("MinFallHeightForDamage") ? FloatSetting("MinFallHeightForDamage") : 40, gravity);
        fallHeightDamageFactor = FloatSetting("FallHeightDamageFactor", 1);
        pitchRollYawFactor = FloatSetting("PitchRollYawFactor", 2);
        crashIntoBuildingWeapon = Setting("VehicleCrashesIntoBuildingWeaponTemplate", "VehicleCrashesIntoBuildingWeapon");
        crashIntoNonBuildingWeapon = Setting("VehicleCrashesIntoNonBuildingWeaponTemplate", "VehicleCrashesIntoNonBuildingWeapon");
    }

    public void OnCreate() { }

    public UpdatePhase Phase => UpdatePhase.Physics;
    public FlagSet DisabledTypesToProcess => DisabledMask.All;

    float Clamp(float f, float min) => Math.Clamp(f, min, MaxFriction);
    float AerodynamicFriction => Clamp(aerodynamicFriction + ExtraFriction, MinAeroFriction);
    float ForwardFriction => Clamp(forwardFriction + ExtraFriction, MinNonAeroFriction);
    float LateralFriction => Clamp(lateralFriction + ExtraFriction, MinNonAeroFriction);

    // ---- Forces ----

    /// <summary>
    /// Adds a force for this frame (acceleration += force / mass). While a locomotor is pushing,
    /// outside forces only act sideways (the locomotor owns forward motion).
    /// </summary>
    public void ApplyForce(Vector3 force)
    {
        if (float.IsNaN(force.X) || float.IsNaN(force.Y) || float.IsNaN(force.Z))
            return;
        var modified = force;
        if (IsMotive)
        {
            var dir = MathUtil.Forward(Owner.Orientation);
            float lateralDot = force.X * -dir.Y + force.Y * dir.X;
            modified.X = lateralDot * -dir.Y;
            modified.Y = lateralDot * dir.X;
        }
        accel += modified / Mass;
        Owner.WakeUpdate(this);
    }

    /// <summary>A locomotor's push: applied in full, and marks the object as being driven for a few frames.</summary>
    public void ApplyMotiveForce(Vector3 force)
    {
        motiveForceExpires = 0;
        ApplyForce(force);
        motiveForceExpires = Frame + MotiveFrames;
    }

    /// <summary>A shove from an explosion; ShockResistance reduces it.</summary>
    public void ApplyShock(Vector3 force) => ApplyForce(force * (1 - Math.Clamp(shockResistance, 0, 1)));

    public void ApplyRandomRotation()
    {
        if (StickToGround)
            return;
        AllowBouncing = true;
        yawRate += shockMaxYaw * Game.Random.Float(-1, 1);
        pitchRate += shockMaxPitch * Game.Random.Float(-1, 1);
        rollRate += shockMaxRoll * Game.Random.Float(-1, 1);
        Owner.WakeUpdate(this);
    }

    public void ClearAcceleration() => accel = Vector3.Zero;

    public void ResetDynamicPhysics()
    {
        accel = prevAccel = velocity = Vector3.Zero;
        Turning = Turning.None;
        yawRate = rollRate = pitchRate = 0;
    }

    public void SetStunned(bool stunned) => IsStunned = stunned;

    public void ScrubVelocity2D(float desired)
    {
        if (desired < 0.001f)
        {
            velocity.X = velocity.Y = 0;
            return;
        }
        float current = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        if (desired > current)
            return;
        velocity.X *= desired / current;
        velocity.Y *= desired / current;
    }

    public void ScrubVelocityZ(float desired)
    {
        if (MathF.Abs(desired) < 0.001f)
            velocity.Z = 0;
        else if (desired < 0 && velocity.Z < desired || desired > 0 && velocity.Z > desired)
            velocity.Z = desired;
    }

    public void AddVelocity(Vector3 v) => velocity += v;
    public void TransferVelocityTo(PhysicsBehavior other) => other.velocity += velocity;
    public void SetIgnoreCollisionsWith(GameObject? obj) => ignoreCollisionsWith = obj?.Id ?? 0;
    bool IsIgnoringCollisionsWith(uint id) => id != 0 && id == ignoreCollisionsWith;

    /// <summary>Speed along the facing in 2D (negative when sliding backwards).</summary>
    public float ForwardSpeed2D
    {
        get
        {
            var dir = MathUtil.Forward(Owner.Orientation);
            float vx = velocity.X * dir.X, vy = velocity.Y * dir.Y;
            float speed = MathF.Sqrt(vx * vx + vy * vy);
            return vx + vy >= 0 ? speed : -speed;
        }
    }

    /// <summary>Speed along the 3D facing (missiles).</summary>
    public float ForwardSpeed3D
    {
        get
        {
            float cp = MathF.Cos(Owner.Pitch);
            var dir = new Vector3(MathF.Cos(Owner.Orientation) * cp, MathF.Sin(Owner.Orientation) * cp, MathF.Sin(Owner.Pitch));
            float vx = velocity.X * dir.X, vy = velocity.Y * dir.Y, vz = velocity.Z * dir.Z;
            float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
            return vx + vy + vz >= 0 ? speed : -speed;
        }
    }

    void ApplyYprDamping(float factor)
    {
        pitchRate *= factor;
        rollRate *= factor;
        yawRate *= factor;
    }

    void ApplyFrictionalForces()
    {
        bool deckTaxiing = Owner.HasStatus(Flags.ObjectStatus.IndexOf("DECK_HEIGHT_OFFSET")) && Owner.AI?.LocomotorSetName == "SET_TAXIING";
        if (AllowAirborneFriction || !Owner.IsSignificantlyAboveTerrain || deckTaxiing)
        {
            ApplyYprDamping(1 - DefaultLateralFriction);
            if (velocity.X != 0 || velocity.Y != 0)
            {
                var dir = MathUtil.Forward(Owner.Orientation);
                float mass = Mass;
                float lateralDot = velocity.X * -dir.Y + velocity.Y * dir.X;
                float lf = mass * LateralFriction;
                var force = new Vector3(-(lf * lateralDot * -dir.Y), -(lf * lateralDot * dir.X), 0);
                if (!IsMotive)
                {
                    float forwardDot = velocity.X * dir.X + velocity.Y * dir.Y;
                    float ff = mass * ForwardFriction;
                    force.X += -(ff * forwardDot * dir.X);
                    force.Y += -(ff * forwardDot * dir.Y);
                }
                ApplyForce(force);
            }
        }
        else
        {
            float aero = -AerodynamicFriction;
            accel += velocity * aero;
            ApplyYprDamping(1 + aero);
        }
    }

    bool HandleBounce(float oldZ, float newZ, float groundZ, out Vector3 bounceForce)
    {
        bounceForce = Vector3.Zero;
        if (!AllowBouncing || newZ > groundZ)
            return false;
        float stiffness = Math.Clamp(Game.Catalog.GameData.GroundStiffness, 0.01f, 0.99f);
        float desiredAccelZ = 0;
        float vz = velocity.Z;
        if (oldZ > groundZ && vz < 0)
            desiredAccelZ = MathF.Abs(vz) * stiffness;
        bounceForce = new Vector3(0, 0, Mass * desiredAccelZ);
        ApplyYprDamping(0.7f);
        if (vz < 0)
        {
            // Land the right way up (or upside down if it was mostly upside down).
            bool upright = MathF.Cos(Owner.Pitch) * MathF.Cos(Owner.Roll) > 0;
            Owner.Pitch = 0;
            Owner.Roll = upright ? 0 : MathF.PI;
        }
        if (bounceForce.Z > 0)
        {
            TestStunnedUnitForDestruction();
            return true;
        }
        AllowBouncing = originalAllowBounce;
        return false;
    }

    // ---- The frame ----

    public int Update()
    {
        var obj = Owner;
        if (MovedByOwnModule)
        {
            accel = Vector3.Zero;
            return 0;
        }
        bool airborneAtStart = obj.IsAboveTerrain;
        float activeVelZ = 0;
        bool gotBounceForce = false;
        var bounceForce = Vector3.Zero;
        if (!updateEverRun)
            wasAirborneLastFrame = airborneAtStart;
        var prevPos = obj.Position;
        prevAccel = accel;

        if (!obj.IsDisabledBy("DISABLED_HELD"))
        {
            accel.Z += Game.Catalog.GameData.Gravity;
            ApplyFrictionalForces();
            velocity += accel;
            const float Thresh = 0.001f;
            if (MathF.Abs(velocity.X) < Thresh) velocity.X = 0;
            if (MathF.Abs(velocity.Y) < Thresh) velocity.Y = 0;
            if (MathF.Abs(velocity.Z) < Thresh) velocity.Z = 0;

            var position = obj.Position;
            float oldZ = position.Z;
            // A braking unit's locomotor places it exactly; physics only moves it vertically.
            if (obj.HasStatus(Flags.ObjectStatus.IndexOf("IS_BRAKING")))
            {
                if (!obj.IsKindOf("PROJECTILE"))
                    position.Z += velocity.Z;
            }
            else
            {
                position += velocity;
            }
            if (float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsNaN(position.Z))
            {
                Game.DestroyObject(obj);
                return LogicTime.SleepForever;
            }

            if (IsStunned && (MathF.Abs(velocity.X) < StunReliefEpsilon && MathF.Abs(velocity.Y) < StunReliefEpsilon && MathF.Abs(velocity.Z) < StunReliefEpsilon
                              || !obj.IsSignificantlyAboveTerrain))
            {
                IsStunned = false;
                obj.SetCondition("STUNNED", false);
            }

            if (HasPitchRollYaw)
            {
                float yaw = yawRate * pitchRollYawFactor, pitch = pitchRate * pitchRollYawFactor, roll = rollRate * pitchRollYawFactor;
                if (centerOfMassOffset != 0)
                {
                    // Nose-heavy objects stop pitching once they point straight down.
                    float remaining = centerOfMassOffset > 0 ? MathF.PI / 2 - obj.Pitch : -MathF.PI / 2 + obj.Pitch;
                    pitch *= MathF.Sin(remaining);
                }
                obj.Roll = MathUtil.NormalizeAngle(obj.Roll + roll);
                obj.Pitch = MathUtil.NormalizeAngle(obj.Pitch - pitch); // positive pitch rate tips the nose down
                obj.SetOrientation(obj.Orientation + yaw);
            }

            float groundZ = Game.Terrain.LayerHeight(position.X, position.Y, obj.Layer);
            gotBounceForce = HandleBounce(oldZ, position.Z, groundZ, out bounceForce);
            activeVelZ = velocity.Z;
            if (position.Z <= groundZ)
            {
                velocity.Z = Math.Min(0, velocity.Z + groundZ - position.Z);
                position.Z = groundZ;
                AllowToFall = false;
                if (IsStunned)
                {
                    obj.SetCondition("STUNNED_FLAILING", false);
                    obj.SetCondition("STUNNED");
                }
            }
            else if (IsInFreeFall)
            {
                obj.SetDisabled("DISABLED_FREEFALL");
                obj.SetCondition("FREEFALL");
            }
            else if (StickToGround && !AllowToFall)
            {
                position.Z = groundZ;
            }
            if (gotBounceForce)
                obj.Pitch = obj.Roll = 0;
            obj.SetPosition(position);
        }

        accel = Vector3.Zero;
        previousOverlap = currentOverlap;
        currentOverlap = 0;
        if (gotBounceForce && AllowBouncing)
            ApplyForce(bounceForce);

        bool airborneAtEnd = obj.IsAboveTerrain;
        if (wasAirborneLastFrame && !airborneAtEnd && !ImmuneToFallingDamage)
        {
            // Landed: tell the object (parachutes let go of their rider) and hurt it if it fell hard.
            obj.OnCollide(null, obj.Position, new Vector3(0, 0, -1));
            float netSpeed = -activeVelZ - minFallSpeedForDamage;
            if (netSpeed > 0 && obj.FindModule<Projectiles.ProjectileModule>() == null)
            {
                const float MinAngleTan = 3, TinyDelta = 0.01f;
                if ((MathF.Abs(velocity.X) <= TinyDelta || MathF.Abs(activeVelZ / velocity.X) >= MinAngleTan)
                    && (MathF.Abs(velocity.Y) <= TinyDelta || MathF.Abs(activeVelZ / velocity.Y) >= MinAngleTan))
                {
                    obj.Damage(new DamageInfo { DamageType = "FALLING", DeathType = "SPLATTED", SourceId = obj.Id, Amount = netSpeed * Mass * fallHeightDamageFactor });
                    if (obj.IsEffectivelyDead)
                        obj.SetCondition("SPLATTED");
                }
            }
        }
        if (!airborneAtEnd)
        {
            IsInFreeFall = false;
            if (obj.IsDisabledBy("DISABLED_FREEFALL"))
                obj.SetDisabled("DISABLED_FREEFALL", false);
            obj.SetCondition("FREEFALL", false);
        }
        if (killWhenRestingOnGround && !airborneAtEnd && MathF.Abs(velocity.X) < 0.01f && MathF.Abs(velocity.Y) < 0.01f && MathF.Abs(velocity.Z) < 0.01f)
        {
            if (!obj.IsKindOf("DRONE") || obj.IsEffectivelyDead || obj.IsDisabledBy("DISABLED_UNMANNED"))
                obj.Kill();
        }
        updateEverRun = true;
        wasAirborneLastFrame = airborneAtEnd;
        return SleepTime;
    }

    int SleepTime =>
        velocity == Vector3.Zero && accel == Vector3.Zero && !HasPitchRollYaw && !IsMotive
        && !Owner.IsAboveTerrain && currentOverlap == 0 && previousOverlap == 0 && updateEverRun
            ? LogicTime.SleepForever : 0;

    void TestStunnedUnitForDestruction()
    {
        if (!IsStunned)
            return;
        var obj = Owner;
        if (MathF.Cos(obj.Pitch) * MathF.Cos(obj.Roll) < 0 || !Game.Terrain.IsInsidePlayableArea(obj.Position.X, obj.Position.Y))
        {
            obj.Kill();
            return;
        }
        if (obj.AI is not { } ai)
            return;
        var (cx, cy) = Game.Terrain.CellAt(obj.Position.X, obj.Position.Y);
        if (Game.Terrain.IsCliffCell(cx, cy) && (ai.Surfaces & Surfaces.Cliff) == 0
            || Game.Terrain.IsUnderwater(obj.Position.X, obj.Position.Y, out _) && (ai.Surfaces & Surfaces.Water) == 0)
            obj.Kill();
    }

    // ---- Collisions ----

    public void OnCollide(GameObject? other, Vector3 location, Vector3 normal)
    {
        var obj = Owner;
        if (obj.FindModule<IProjectile>() is { } projectile && projectile.HandleCollision(other))
            return;
        var containedBy = Game.FindObject(obj.ContainedById);
        if (other == null)
        {
            containedBy?.OnCollide(null, location, normal);
            return;
        }
        if (other.ContainedById == obj.Id || containedBy == other)
            return;
        if (obj.HasStatus(Flags.ObjectStatus.IndexOf("PARACHUTING")) && other.HasStatus(Flags.ObjectStatus.IndexOf("PARACHUTING")))
            return;

        var ai = obj.AI;
        if (ai != null && ai.IgnoredObstacleId == other.Id)
        {
            // A pilot reaching an empty vehicle climbs in.
            if (obj.IsKindOf(Kind.Infantry) && other.IsDisabledBy("DISABLED_UNMANNED"))
            {
                other.SetDisabled("DISABLED_UNMANNED", false);
                other.Defect(obj.Team ?? obj.Owner.DefaultTeam, 0);
                Game.Scripts?.TransferObjectName(obj, other);
                Game.DestroyObject(obj);
            }
            return;
        }
        if (other.AI is { } otherAi && otherAi.IgnoredObstacleId == obj.Id)
            return;
        if (IsIgnoringCollisionsWith(other.Id))
            return;

        bool immobile = obj.IsKindOf(Kind.Immobile);
        bool otherImmobile = other.IsKindOf(Kind.Immobile);
        var otherPhysics = other.FindModule<PhysicsBehavior>();
        if (otherPhysics != null)
        {
            if (otherPhysics.IsIgnoringCollisionsWith(obj.Id))
                return;
        }
        else if (!otherImmobile)
        {
            return;
        }

        if (CheckForOverlapCollision(other))
            return;

        if (ai != null && !((obj.IsEffectivelyDead || obj.HasStatus(Flags.ObjectStatus.IndexOf("PARACHUTING"))) && otherImmobile))
        {
            if (!ai.ProcessCollision(this, other))
                return;
        }

        var usCenter = obj.Position + new Vector3(0, 0, obj.Geometry.ZDeltaToCenterPosition);
        var themCenter = other.Position + new Vector3(0, 0, other.Geometry.ZDeltaToCenterPosition);
        var delta = themCenter - usCenter;
        float distSqr, usRadius, themRadius;
        if (obj.IsAboveTerrain)
        {
            usRadius = obj.BoundingSphereRadius;
            themRadius = other.BoundingSphereRadius;
            distSqr = delta.LengthSquared();
        }
        else
        {
            usRadius = obj.BoundingCircleRadius;
            themRadius = other.BoundingCircleRadius;
            delta.Z = 0;
            distSqr = delta.LengthSquared();
        }
        if (distSqr > (usRadius + themRadius) * (usRadius + themRadius))
            return;

        LastCollideeId = other.Id;
        float dist = MathF.Sqrt(distSqr);
        float overlap = usRadius + themRadius - dist;
        if (dist < 1)
            dist = 1;
        if (!AllowCollideForce)
            return;

        float factor;
        if (otherImmobile && !obj.IsDestroyed)
        {
            if (obj.HasStatus(Flags.ObjectStatus.IndexOf("PARACHUTING")))
            {
                // Parachutes slide off buildings.
                var toBounce = obj;
                while (Game.FindObject(toBounce.ContainedById) is { } container)
                    toBounce = container;
                float bounceOut = usRadius * 0.1f;
                toBounce.SetPosition(toBounce.Position - new Vector3(bounceOut * delta.X / dist, bounceOut * delta.Y / dist, 0));
                toBounce.FindModule<PhysicsBehavior>()?.ScrubVelocity2D(0);
                return;
            }
            float stiffness = Math.Clamp(Game.Catalog.GameData.StructureStiffness, 0.01f, 0.99f);
            float mag = Math.Max(VelocityMagnitude, 1f / (LogicTime.FramesPerSecond * 5f));
            factor = -mag * Mass * stiffness;
            if (delta.Z < 0 && obj.Position.Z >= Game.Catalog.GameData.DefaultStructureRubbleHeight)
            {
                // Something falling onto a building.
                if (other.IsKindOf(Kind.Structure))
                {
                    if (obj.IsKindOf("VEHICLE"))
                        Game.Damage.FireTemporaryWeapon(crashIntoBuildingWeapon, obj, obj.Position);
                    Game.DestroyObject(obj);
                    return;
                }
                if (obj.IsKindOf("VEHICLE"))
                    Game.Damage.FireTemporaryWeapon(crashIntoNonBuildingWeapon, obj, obj.Position);
            }
            velocity = Vector3.Zero;
        }
        else
        {
            factor = -Math.Min(overlap, 5f);
        }
        ApplyForce(new Vector3(factor * delta.X / dist, factor * delta.Y / dist, factor * delta.Z / dist));
    }

    enum CrushTarget { None, Total, FrontEnd, BackEnd }

    static bool PerpsLogicallyEqual(float a, float b) => MathF.Abs(a - b) <= 0.15f;

    /// <summary>Tanks rolling over infantry and small vehicles: returns true if this was a crush (no push apart).</summary>
    bool CheckForOverlapCollision(GameObject other)
    {
        if (MathF.Abs(velocity.X) < 0.01f && MathF.Abs(velocity.Y) < 0.01f && MathF.Abs(velocity.Z) < 0.01f)
            return false;
        var crusher = Owner;
        bool selfCrushingOther = crusher.CanCrushOrSquish(other, crushOnly: true);
        bool selfBeingCrushed = other.CanCrushOrSquish(crusher, crushOnly: true);
        if (selfCrushingOther && selfBeingCrushed)
            return false;
        if (selfBeingCrushed)
            return true;
        if (!selfCrushingOther)
            return false;

        if (currentOverlap != other.Id)
            currentOverlap = other.Id;
        if (previousOverlap != other.Id)
            other.Damage(new DamageInfo { DamageType = "CRUSH", DeathType = "CRUSHED", SourceId = crusher.Id, Amount = 0 });

        if (other.Body is not { } crusheeBody || crusheeBody.FrontCrushed && crusheeBody.BackCrushed)
            return true;

        var crusheePos = other.Position;
        var crusherPos = crusher.Position;
        var dir = MathUtil.Forward(crusher.Orientation);
        var crusheeDir = MathUtil.Forward(other.Orientation);
        float offsetDistance = other.Geometry.MajorRadius / 2;
        var crushPointOffset = crusheeDir * offsetDistance;
        CrushTarget target;
        if (crusheeBody.FrontCrushed || crusheeBody.BackCrushed)
        {
            target = crusheeBody.FrontCrushed ? CrushTarget.BackEnd : CrushTarget.FrontEnd;
        }
        else
        {
            // Which of the victim's front, centre and back is closest to the crusher's line of travel?
            float Perp(Vector3 point, out float length)
            {
                var v = (point - crusherPos) with { Z = 0 };
                length = v.Length();
                float ray = v.X * dir.X + v.Y * dir.Y;
                return new Vector3(ray * dir.X - v.X, ray * dir.Y - v.Y, 0).Length();
            }
            float front = Perp(crusheePos + crushPointOffset, out float frontLength);
            float back = Perp(crusheePos - crushPointOffset, out float backLength);
            float center = Perp(crusheePos, out float centerLength);
            if (front <= center && front <= back)
            {
                if (PerpsLogicallyEqual(front, center))
                    target = frontLength < centerLength ? CrushTarget.FrontEnd : CrushTarget.Total;
                else if (PerpsLogicallyEqual(front, back))
                    target = frontLength < backLength ? CrushTarget.FrontEnd : CrushTarget.BackEnd;
                else
                    target = CrushTarget.FrontEnd;
            }
            else if (back <= center && back <= front)
            {
                if (PerpsLogicallyEqual(back, center))
                    target = backLength < centerLength ? CrushTarget.BackEnd : CrushTarget.Total;
                else if (PerpsLogicallyEqual(back, front))
                    target = backLength < frontLength ? CrushTarget.BackEnd : CrushTarget.FrontEnd;
                else
                    target = CrushTarget.BackEnd;
            }
            else
            {
                if (PerpsLogicallyEqual(center, front))
                    target = centerLength < frontLength ? CrushTarget.Total : CrushTarget.FrontEnd;
                else if (PerpsLogicallyEqual(center, back))
                    target = centerLength < backLength ? CrushTarget.Total : CrushTarget.BackEnd;
                else
                    target = CrushTarget.Total;
            }
        }

        float tooFarSquared = 2.25f * offsetDistance * offsetDistance;
        var point = target switch
        {
            CrushTarget.FrontEnd => crusheePos + crushPointOffset,
            CrushTarget.BackEnd => crusheePos - crushPointOffset,
            _ => crusheePos,
        };
        float dx = point.X - crusherPos.X, dy = point.Y - crusherPos.Y;
        // The crusher's centre has passed the crush point.
        if (target != CrushTarget.None && dir.X * dx + dir.Y * dy < 0 && dx * dx + dy * dy < tooFarSquared)
            other.Damage(new DamageInfo { DamageType = "CRUSH", DeathType = "CRUSHED", SourceId = crusher.Id, Amount = DamageInfo.HugeDamage });
        return true;
    }
}
