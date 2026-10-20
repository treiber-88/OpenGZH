using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Projectiles;

/// <summary>Shared projectile behaviour: remembers who fired what at whom, and detonates.</summary>
public abstract class ProjectileModule : Module, IUpdateModule, IProjectile
{
    protected GameObject? Launcher;
    protected WeaponTemplate? Weapon;
    protected uint TargetId;
    protected Vector3 TargetPosition;
    protected WeaponBonus Bonus = WeaponBonus.None;
    protected bool Launched;
    bool detonated;

    protected GameObject? Target => Game.FindObject(TargetId) is { IsEffectivelyDead: false } t ? t : null;

    public virtual void Launch(GameObject launcher, WeaponTemplate weapon, GameObject? target, Vector3 targetPosition, WeaponBonus bonus)
    {
        Launcher = launcher;
        Weapon = weapon;
        TargetId = target?.Id ?? 0;
        TargetPosition = targetPosition;
        Bonus = bonus;
        Launched = true;
        // These projectiles fly their own course; physics leaves them alone (collisions still apply).
        if (Owner.FindModule<PhysicsBehavior>() is { } physics)
            physics.MovedByOwnModule = true;
        Owner.WakeUpdate(this);
    }

    public abstract int Update();

    /// <summary>Whether the projectile has started flying (missiles arm after launch).</summary>
    protected virtual bool IsArmed => Launched;

    /// <summary>
    /// The projectile touched something (null = the ground). Returns true when handled, so physics
    /// doesn't also push it around: it ignores what its weapon passes through, can clear a garrison
    /// (GarrisonHitKillCount), and otherwise detonates.
    /// </summary>
    public virtual bool HandleCollision(GameObject? other)
    {
        if (!IsArmed || Weapon == null)
            return true;
        if (other != null)
        {
            if (!Weapon.ShouldProjectileCollideWith(Launcher, Owner, other, TargetId))
                return true;
            int killCount = IntSetting("GarrisonHitKillCount");
            if (killCount > 0 && other.FindModule<Contain.ContainModule>() is { IsGarrisonable: true, Count: > 0, IsImmuneToClearBuildingAttacks: false } garrison)
            {
                var required = Flags.KindOf.Parse(Setting("GarrisonHitKillRequiredKindOf"));
                var forbidden = Flags.KindOf.Parse(Setting("GarrisonHitKillForbiddenKindOf"));
                int killed = 0;
                foreach (var occupant in garrison.Passengers.ToList())
                {
                    if (killed >= killCount)
                        break;
                    if (occupant.IsEffectivelyDead || !occupant.KindOf.HasAll(required) || occupant.KindOf.HasAny(forbidden))
                        continue;
                    if (Launcher != null)
                        Launcher.Owner.ScoreKill(occupant);
                    occupant.Kill();
                    killed++;
                }
                if (killed > 0)
                {
                    Game.Events.PlayFx(Setting("GarrisonHitKillFX"), other.Position, other);
                    Game.DestroyObject(Owner);
                    return true;
                }
            }
        }
        else if (!HitsGround())
        {
            return true;
        }
        Detonate(other);
        Owner.SetStatus(Core.Status.NoCollisions);
        return true;
    }

    /// <summary>Whether touching the ground sets it off (missiles flying high over terrain bumps don't).</summary>
    protected virtual bool HitsGround() => true;

    /// <summary>Deals the weapon's damage where the projectile is, plays its effects, and removes it.</summary>
    protected void Detonate(GameObject? directHit)
    {
        if (detonated)
            return;
        detonated = true;
        if (Weapon != null)
        {
            int vet = (int)(Launcher?.Veterancy ?? VeterancyLevel.Regular);
            Game.Events.PlayFx(Weapon.ProjectileDetonationFX[vet], Owner.Position);
            if (Weapon.ProjectileDetonationOCL[vet] is { Length: > 0 } ocl)
                Game.ObjectCreation.Run(ocl, Launcher ?? Owner, Owner.Position);
            Game.Damage.DealWeaponDamage(Weapon, Launcher, directHit, Owner.Position, Bonus);
        }
        if (BoolSetting("DetonateCallsKill"))
            Owner.Kill();
        else
            Game.DestroyObject(Owner);
    }
}

/// <summary>
/// An unguided shell or bomb flying a fixed curve to where the target was (tank shells, artillery).
/// The curve is the original's: a cubic Bezier from launch to target whose two control points sit at
/// FirstPercentIndent / SecondPercentIndent along the way, raised by FirstHeight / SecondHeight.
/// Moving targets are tracked by at most FlightPathAdjustDistPerSecond.
/// </summary>
public sealed class DumbProjectileBehavior : ProjectileModule
{
    Vector3 start, control1, control2, end;
    float travelled, length, speed;
    float firstHeight, secondHeight, firstIndent, secondIndent, adjustPerFrame;

    protected override void Initialize()
    {
        firstHeight = FloatSetting("FirstHeight");
        secondHeight = FloatSetting("SecondHeight");
        firstIndent = FloatSetting("FirstPercentIndent", 0.3f);
        secondIndent = FloatSetting("SecondPercentIndent", 0.7f);
        adjustPerFrame = RuleUnits.Velocity(Setting("FlightPathAdjustDistPerSecond"));
    }

    public override void Launch(GameObject launcher, WeaponTemplate weapon, GameObject? target, Vector3 targetPosition, WeaponBonus bonus)
    {
        base.Launch(launcher, weapon, target, targetPosition, bonus);
        start = Owner.Position;
        end = target != null ? target.Position + new Vector3(0, 0, target.Height * 0.5f) : targetPosition;
        BuildCurve();
        speed = Math.Max(0.5f, weapon.WeaponSpeed);
        if (weapon.ScaleWeaponSpeed)
            speed = Math.Max(weapon.MinWeaponSpeed, speed * Math.Clamp(length / Math.Max(1, weapon.AttackRange), 0.1f, 1));
    }

    void BuildCurve()
    {
        var line = end - start;
        control1 = start + line * firstIndent + new Vector3(0, 0, firstHeight);
        control2 = start + line * secondIndent + new Vector3(0, 0, secondHeight);
        length = Math.Max(1, line.Length());
    }

    Vector3 PointAt(float t)
    {
        float u = 1 - t;
        return u * u * u * start + 3 * u * u * t * control1 + 3 * u * t * t * control2 + t * t * t * end;
    }

    public override int Update()
    {
        if (!Launched)
            return 0;

        // Follow a moving target a little.
        if (Target is { } target && adjustPerFrame > 0)
        {
            var wanted = target.Position + new Vector3(0, 0, target.Height * 0.5f);
            var shift = wanted - end;
            if (shift.Length() > adjustPerFrame)
                shift = Vector3.Normalize(shift) * adjustPerFrame;
            end += shift;
            BuildCurve();
        }

        travelled += speed;
        float t = Math.Min(1, travelled / length);
        var previous = Owner.Position;
        var position = PointAt(t);
        Owner.SetPosition(position);
        var direction = position - previous;
        if (direction.LengthSquared() > 0.0001f)
        {
            Owner.SetOrientation(MathF.Atan2(direction.Y, direction.X));
            Owner.Pitch = MathF.Atan2(direction.Z, new Vector2(direction.X, direction.Y).Length());
        }

        bool hitGround = t > 0.2f && position.Z < Game.Terrain.LayerHeight(position.X, position.Y) - 1;
        if (t >= 1 || hitGround)
            Detonate(t >= 1 ? Target : null);
        return 0;
    }
}

/// <summary>
/// A guided missile: ignites after IgnitionDelay, flies straight for DistanceToTravelBeforeTurning,
/// then steers towards its target (TryToFollowTarget) using its locomotor's speed and turn rate,
/// until it hits or its fuel runs out (FuelLifetime).
/// </summary>
public sealed class MissileAIUpdate : ProjectileModule
{
    // An AI in the original, so it steers in the initial phase.
    public UpdatePhase Phase => UpdatePhase.Initial;
    public FlagSet DisabledTypesToProcess => DisabledMask.Held;

    float velocity, maxSpeed, acceleration, turnRate;
    int ignitionDelay, fuelFrames, launchFrame;
    float straightDistance, travelled;
    bool followTarget, detonateOnNoFuel;
    Vector3 heading;

    protected override void Initialize()
    {
        velocity = RuleUnits.Velocity(Setting("InitialVelocity"));
        ignitionDelay = FramesSetting("IgnitionDelay");
        fuelFrames = FramesSetting("FuelLifetime", 10000);
        straightDistance = FloatSetting("DistanceToTravelBeforeTurning");
        followTarget = BoolSetting("TryToFollowTarget", true);
        detonateOnNoFuel = BoolSetting("DetonateOnNoFuel");
    }

    public override void Launch(GameObject launcher, WeaponTemplate weapon, GameObject? target, Vector3 targetPosition, WeaponBonus bonus)
    {
        base.Launch(launcher, weapon, target, targetPosition, bonus);
        launchFrame = Frame;
        heading = MathUtil.Forward(launcher.Orientation) with { Z = 0.3f };
        heading = Vector3.Normalize(heading);

        // Speed and steering come from the missile's own locomotor (e.g. "TomahawkMissileLocomotor").
        if (Owner.Template.Rules.TryGet("Locomotor", out var locomotors))
        {
            string name = Core.RuleWords.Second(locomotors.AsList().FirstOrDefault()?.Value);
            if (Game.Catalog.Locomotor(name) is { } locomotor)
            {
                maxSpeed = locomotor.Speed;
                acceleration = Math.Max(locomotor.Acceleration, maxSpeed / 15);
                turnRate = locomotor.TurnRate;
            }
        }
        if (maxSpeed <= 0)
        {
            maxSpeed = Math.Max(weapon.WeaponSpeed, 5);
            acceleration = maxSpeed / 10;
            turnRate = MathF.PI / 15;
        }
    }

    public override int Update()
    {
        if (!Launched)
            return 0;
        int age = Frame - launchFrame;
        if (age >= ignitionDelay)
            velocity = Math.Min(maxSpeed, velocity + acceleration);

        var aim = Target is { } target ? target.Position + new Vector3(0, 0, target.Height * 0.5f) : TargetPosition;
        if (followTarget && travelled >= straightDistance)
        {
            var wanted = aim - Owner.Position;
            if (wanted.LengthSquared() > 0.01f)
            {
                wanted = Vector3.Normalize(wanted);
                // Turn by at most turnRate per frame towards the target.
                float angle = MathF.Acos(Math.Clamp(Vector3.Dot(heading, wanted), -1, 1));
                heading = angle <= turnRate || angle < 0.0001f ? wanted : Vector3.Normalize(Vector3.Lerp(heading, wanted, turnRate / angle));
            }
        }

        var position = Owner.Position + heading * velocity;
        travelled += velocity;
        Owner.SetPosition(position);
        Owner.SetOrientation(MathF.Atan2(heading.Y, heading.X));
        Owner.Pitch = MathF.Asin(Math.Clamp(heading.Z, -1, 1));

        float hitDistance = Math.Max(velocity, 3) + (Target?.Radius ?? 0);
        if (Vector3.Distance(position, aim) <= hitDistance)
            Detonate(Target);
        else if (position.Z < Game.Terrain.LayerHeight(position.X, position.Y) && travelled > 10)
            Detonate(null);
        else if (age > fuelFrames)
        {
            if (detonateOnNoFuel)
                Detonate(null);
            else
            {
                // Out of fuel: drop to the ground and detonate there.
                heading = Vector3.Normalize(heading + new Vector3(0, 0, -0.08f));
            }
        }
        return 0;
    }
}
