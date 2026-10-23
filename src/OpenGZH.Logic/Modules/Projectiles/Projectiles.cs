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
            if (ProjectileHits.TryClearGarrison(this, Owner, Launcher, other))
                return true;
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
            ProjectileHits.Detonate(Weapon, Owner, Launcher, directHit, Bonus);
        if (BoolSetting("DetonateCallsKill"))
            Owner.Kill();
        else
            Game.DestroyObject(Owner);
    }
}

/// <summary>
/// An unguided shell or bomb (the original's DumbProjectileBehavior). Its flight is a cubic Bezier
/// curve from launch to the target's centre, worked out in advance as one point per frame (curve
/// length / weapon speed points). The two inner control points sit FirstPercentIndent and
/// SecondPercentIndent along the line, FirstHeight / SecondHeight above the highest terrain in
/// between. ScaleWeaponSpeed weapons fly faster the further the target (MinWeaponSpeed at minimum
/// range up to WeaponSpeed at full range). A moving target drags the end of the curve by up to
/// FlightPathAdjustDistPerSecond. It goes off at the end of the curve, on touching something or the
/// ground, or after MaxLifespan. TumbleRandomly shells spin; others point along their path
/// (OrientToFlightPath).
/// </summary>
public sealed class DumbProjectileBehavior : ProjectileModule
{
    const int DefaultMaxLifespan = 10 * LogicTime.FramesPerSecond;

    int maxLifespan;
    bool tumbleRandomly, orientToFlightPath;
    float firstHeight, secondHeight, firstIndent, secondIndent, adjustPerFrame;

    int lifespanFrame;
    readonly List<Vector3> flightPath = new();
    int segments;
    float speed;
    Vector3 pathStart, pathEnd;
    int step;
    float yawRate, pitchRate, rollRate;

    protected override void Initialize()
    {
        maxLifespan = Definition.Settings.Has("MaxLifespan") ? FramesSetting("MaxLifespan") : DefaultMaxLifespan;
        tumbleRandomly = BoolSetting("TumbleRandomly");
        orientToFlightPath = BoolSetting("OrientToFlightPath", true);
        firstHeight = FloatSetting("FirstHeight");
        secondHeight = FloatSetting("SecondHeight");
        firstIndent = FloatSetting("FirstPercentIndent");
        secondIndent = FloatSetting("SecondPercentIndent");
        adjustPerFrame = RuleUnits.Velocity(Setting("FlightPathAdjustDistPerSecond"));
    }

    static Vector3 CenterOf(GameObject obj) =>
        obj.Position + new Vector3(0, 0, obj.Geometry.Type == GeometryType.Sphere ? 0 : obj.Geometry.Height * 0.5f);

    public override void Launch(GameObject launcher, WeaponTemplate weapon, GameObject? target, Vector3 targetPosition, WeaponBonus bonus)
    {
        base.Launch(launcher, weapon, target, targetPosition, bonus);
        lifespanFrame = Frame + maxLifespan;
        var victimPos = target != null ? CenterOf(target) : targetPosition;

        if (weapon.ScaleWeaponSpeed)
        {
            float minRange = weapon.MinimumAttackRange, maxRange = weapon.AttackRange;
            float range = MathUtil.Distance2D(Owner.Position, victimPos);
            float ratio = maxRange > minRange ? (range - minRange) / (maxRange - minRange) : 1;
            speed = ratio * (weapon.WeaponSpeed - weapon.MinWeaponSpeed) + weapon.MinWeaponSpeed;
        }
        else
            speed = weapon.WeaponSpeed;
        speed = Math.Max(speed, 0.01f);

        if (tumbleRandomly)
        {
            pitchRate = Game.Random.Float(-1 / MathF.PI, 1 / MathF.PI);
            yawRate = Game.Random.Float(-1 / MathF.PI, 1 / MathF.PI);
            rollRate = Game.Random.Float(-1 / MathF.PI, 1 / MathF.PI);
        }

        pathStart = Owner.Position;
        pathEnd = victimPos;
        if (!CalcFlightPath(recalcSegments: true))
        {
            Game.DestroyObject(Owner);
            return;
        }
        step = 0;
    }

    /// <summary>The highest ground under the line, partition cell by cell (the original's terrain-extremes scan).</summary>
    bool HighestTerrainAlong(Vector3 a, Vector3 b, out float highest)
    {
        highest = float.MinValue;
        var terrain = Game.Terrain;
        if (!terrain.IsInsidePlayableArea(a.X, a.Y) && !terrain.IsInsidePlayableArea(b.X, b.Y))
            return false;
        const float CellSize = 100f;
        float length = MathUtil.Distance2D(a, b);
        int samples = Math.Max(1, (int)MathF.Ceiling(length / (CellSize * 0.25f)));
        for (int i = 0; i <= samples; i++)
        {
            var p = Vector3.Lerp(a, b, i / (float)samples);
            highest = Math.Max(highest, terrain.GroundHeight(p.X, p.Y));
        }
        return true;
    }

    bool CalcFlightPath(bool recalcSegments)
    {
        if (!HighestTerrainAlong(pathStart, pathEnd, out float highest))
            return false;
        var line = pathEnd - pathStart;
        float distance = line.Length();
        var dir = distance > 0 ? line / distance : Vector3.Zero;
        var c0 = pathStart;
        var c3 = pathEnd;
        var c1 = pathStart + dir * (distance * firstIndent);
        var c2 = pathStart + dir * (distance * secondIndent);
        highest = Math.Max(highest, Math.Max(c0.Z, c3.Z));
        c1.Z = highest + firstHeight;
        c2.Z = highest + secondHeight;

        if (recalcSegments)
            segments = (int)MathF.Ceiling(ApproximateLength(c0, c1, c2, c3, 0.5f) / speed);

        flightPath.Clear();
        if (segments <= 1)
        {
            // The original's iterator starts at the origin in this case.
            for (int i = 0; i < segments; i++)
                flightPath.Add(Vector3.Zero);
            return true;
        }
        for (int i = 0; i < segments; i++)
        {
            float t = i / (float)(segments - 1);
            float u = 1 - t;
            flightPath.Add(u * u * u * c0 + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * c3);
        }
        return true;
    }

    /// <summary>The original's recursive Bezier length estimate.</summary>
    static float ApproximateLength(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float tolerance)
    {
        float chord = Vector3.Distance(p0, p3);
        float hull = Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, p3);
        if (hull - chord > tolerance)
        {
            // Split at t = 0.5 (de Casteljau).
            var a = (p0 + p1) * 0.5f;
            var b = (p1 + p2) * 0.5f;
            var c = (p2 + p3) * 0.5f;
            var d = (a + b) * 0.5f;
            var e = (b + c) * 0.5f;
            var m = (d + e) * 0.5f;
            return ApproximateLength(p0, a, d, m, tolerance) + ApproximateLength(m, e, c, p3, tolerance);
        }
        return (int)(chord + hull) / 2f;
    }

    public override int Update()
    {
        if (!Launched)
            return 0;
        if (lifespanFrame != 0 && Frame >= lifespanFrame || step >= flightPath.Count)
        {
            Detonate(null);
            return 0;
        }

        // Follow a moving target a little.
        if (TargetId != 0 && adjustPerFrame > 0 && Game.FindObject(TargetId) is { } victim)
        {
            var delta = CenterOf(victim) - pathEnd;
            float moved = delta.Length();
            if (moved * moved > 0.1f)
            {
                pathEnd += delta / moved * Math.Min(moved, adjustPerFrame);
                if (!CalcFlightPath(recalcSegments: false))
                {
                    Detonate(null);
                    return 0;
                }
            }
        }

        var position = flightPath[step];
        if (orientToFlightPath && !tumbleRandomly && flightPath.Count > 1)
        {
            var dir = step > 0 ? position - flightPath[step - 1] : flightPath[1] - flightPath[0];
            if (dir.LengthSquared() > 0)
            {
                Owner.SetOrientation(MathF.Atan2(dir.Y, dir.X));
                Owner.Pitch = MathF.Atan2(dir.Z, new Vector2(dir.X, dir.Y).Length());
            }
        }
        else if (tumbleRandomly)
        {
            Owner.SetOrientation(Owner.Orientation + yawRate);
            Owner.Pitch += pitchRate;
            Owner.Roll += rollRate;
        }
        Owner.SetPosition(position);

        // Touching the ground (the original's physics reports this as a collision).
        if (step > 0 && position.Z <= Game.Terrain.HighestLayerHeight(position.X, position.Y))
        {
            HandleCollision(null);
            if (Owner.IsDestroyed)
                return 0;
        }
        step++;
        return 0;
    }
}

/// <summary>What happens when any projectile hits: clearing garrisons, and detonating.</summary>
public static class ProjectileHits
{
    /// <summary>
    /// A projectile with GarrisonHitKillCount hitting an occupied garrison kills up to that many of the
    /// occupants (with GarrisonHitKillRequiredKindOf / without GarrisonHitKillForbiddenKindOf) and vanishes.
    /// </summary>
    public static bool TryClearGarrison(Module projectileModule, GameObject projectile, GameObject? launcher, GameObject other)
    {
        int killCount = projectileModule.Definition.GetInt("GarrisonHitKillCount");
        if (killCount <= 0 || other.Contain is not { IsGarrisonable: true, Count: > 0, IsImmuneToClearBuildingAttacks: false } garrison)
            return false;
        var required = Flags.KindOf.Parse(projectileModule.Definition.Get("GarrisonHitKillRequiredKindOf"));
        var forbidden = Flags.KindOf.Parse(projectileModule.Definition.Get("GarrisonHitKillForbiddenKindOf"));
        int killed = 0;
        foreach (var occupant in garrison.Passengers.ToList())
        {
            if (killed >= killCount)
                break;
            if (occupant.IsEffectivelyDead || !occupant.KindOf.HasAll(required) || occupant.KindOf.HasAny(forbidden))
                continue;
            launcher?.ScoreTheKill(occupant);
            occupant.Kill();
            killed++;
        }
        if (killed == 0)
            return false;
        projectile.Game.Events.PlayFx(projectileModule.Definition.Get("GarrisonHitKillFX"), other.Position, other);
        projectile.Game.DestroyObject(projectile);
        return true;
    }

    /// <summary>The weapon's detonation where the projectile is: effects, creation list and damage (the original's handleProjectileDetonation).</summary>
    public static void Detonate(WeaponTemplate weapon, GameObject projectile, GameObject? launcher, GameObject? directHit, WeaponBonus bonus, bool doDamage = true)
    {
        var game = projectile.Game;
        int vet = (int)(launcher?.Veterancy ?? VeterancyLevel.Regular);
        game.Events.PlayFx(weapon.ProjectileDetonationFX[vet], projectile.Position);
        if (weapon.ProjectileDetonationOCL[vet] is { Length: > 0 } ocl)
            game.ObjectCreation.Run(ocl, launcher ?? projectile, projectile.Position);
        if (doDamage)
            game.Damage.DealWeaponDamage(weapon, launcher, directHit, projectile.Position, bonus);
    }
}
