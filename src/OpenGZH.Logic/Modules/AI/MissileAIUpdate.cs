using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Locomotion;
using OpenGZH.Logic.Modules.Projectiles;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.AI;

/// <summary>
/// A guided missile, flown by its own locomotor like any aircraft (the original's MissileAIUpdate).
///
/// Launched with a push of InitialVelocity (or the weapon's speed, UseWeaponSpeed) along the launcher's
/// facing, tilted up towards a higher target. After IgnitionDelay it ignites (IgnitionFX, exhaust)
/// and is armed; it then flies DistanceToTravelBeforeTurning without turning before homing in on its
/// target (TryToFollowTarget) or the target point. Within DistanceToTargetForLock it locks on and
/// makes its final run; within DistanceToTargetBeforeDiving it dives. Fuel lasts FuelLifetime: with
/// DetonateOnNoFuel it then goes off, otherwise it coasts down. If its target disappears, it destroys
/// itself. Hitting ground far from its goal (over hills) doesn't count.
/// </summary>
public sealed class MissileAIUpdate : AIUpdateInterface, IProjectile
{
    const float BigNumber = LocomotorTemplate.BigNumber;
    const float ApproachHeight = 10.0f;

    enum MissileState { Prelaunch, Launch, Ignition, AttackNoTurn, Attack, Kill, KillSelf, Dead }

    // Settings
    bool tryToFollowTarget, useWeaponSpeed, detonateOnNoFuel, detonateCallsKill;
    int fuelLifetime, ignitionDelay, killSelfDelay;
    float initialVelocity, initialDistance, diveDistance, lockDistance, scatterWhenJammed;
    string ignitionFx = "";

    MissileState state = MissileState.Prelaunch;
    int stateTimestamp;
    uint launcherId, victimId, goalObjectId;
    Vector3 goalPosition, originalTargetPos, prevPos;
    bool isArmed, isTrackingTarget, noDamage, isJammed;
    int fuelExpirationFrame;
    float noTurnDistLeft;
    float maxAccel = BigNumber;
    WeaponTemplate? detonationWeapon;
    WeaponBonus bonus = WeaponBonus.None;
    string exhaust = "";
    int framesTillDecoyed;
    PhysicsBehavior? physics;

    protected override void Initialize()
    {
        base.Initialize();
        tryToFollowTarget = BoolSetting("TryToFollowTarget", true);
        fuelLifetime = FramesSetting("FuelLifetime");
        ignitionDelay = FramesSetting("IgnitionDelay");
        initialVelocity = RuleUnits.Velocity(Setting("InitialVelocity"));
        initialDistance = FloatSetting("DistanceToTravelBeforeTurning");
        diveDistance = FloatSetting("DistanceToTargetBeforeDiving");
        lockDistance = FloatSetting("DistanceToTargetForLock", 75);
        ignitionFx = Setting("IgnitionFX");
        useWeaponSpeed = BoolSetting("UseWeaponSpeed");
        detonateOnNoFuel = BoolSetting("DetonateOnNoFuel");
        scatterWhenJammed = FloatSetting("DistanceScatterWhenJammed", 75);
        detonateCallsKill = BoolSetting("DetonateCallsKill");
        killSelfDelay = Definition.Settings.Has("KillSelfDelay") ? FramesSetting("KillSelfDelay") : 3;
        noTurnDistLeft = initialDistance;
    }

    public override void OnCreate()
    {
        base.OnCreate();
        physics = Owner.FindModule<PhysicsBehavior>();
        stateTimestamp = Frame;
        prevPos = Owner.Position;
    }

    public bool IsArmed => isArmed;
    public uint LauncherId => launcherId;

    void SwitchTo(MissileState next)
    {
        if (state == next)
            return;
        state = next;
        stateTimestamp = Frame;
    }

    GameObject? GoalObject => goalObjectId != 0 && Game.FindObject(goalObjectId) is { IsEffectivelyDead: false } g ? g : null;

    // ---- Launching ----

    public void Launch(GameObject launcher, WeaponTemplate weapon, GameObject? target, Vector3 targetPosition, WeaponBonus weaponBonus)
    {
        launcherId = launcher.Id;
        detonationWeapon = weapon;
        bonus = weaponBonus;
        exhaust = weapon.ProjectileExhaust[(int)launcher.Veterancy];
        FireAt(target, target?.Position ?? targetPosition);
    }

    void FireAt(GameObject? victim, Vector3 victimPos)
    {
        float initialVel = initialVelocity;
        if (useWeaponSpeed && detonationWeapon != null)
        {
            initialVel = detonationWeapon.WeaponSpeed;
            if (CurrentLocomotor is { } loco)
            {
                maxAccel = initialVel;
                loco.SetMaxSpeed(initialVel);
                loco.SetMaxAcceleration(maxAccel);
            }
        }

        // Tilt upwards towards a target that is higher up.
        float deltaZ = victimPos.Z - Owner.Position.Z;
        float xyDist = Math.Max(1, MathUtil.Distance2D(victimPos, Owner.Position));
        float zFactor = deltaZ > 0 ? deltaZ / xyDist : 0;
        // The launch direction is the way the missile points (it leaves along its launch bone).
        var dir = new Vector3(MathF.Cos(Owner.Orientation) * MathF.Cos(Owner.Pitch), MathF.Sin(Owner.Orientation) * MathF.Cos(Owner.Pitch), MathF.Sin(Owner.Pitch));
        dir = Vector3.Normalize(dir);
        dir.Z += 2 * zFactor;
        dir = Vector3.Normalize(dir);
        if (physics != null && initialVel > 0)
            physics.ApplyMotiveForce(dir * physics.Mass * initialVel);
        Owner.SetOrientation(MathF.Atan2(dir.Y, dir.X));
        Owner.Pitch = MathF.Asin(Math.Clamp(dir.Z, -1, 1));

        SwitchTo(MissileState.Launch);
        isTrackingTarget = false;
        if (victim != null && tryToFollowTarget)
        {
            goalObjectId = victim.Id;
            goalPosition = victim.Position;
            originalTargetPos = victim.Position;
            isTrackingTarget = true;
            victimId = victim.Id;
        }
        else
        {
            var initialPos = victimPos;
            originalTargetPos = victimPos;
            if (lockDistance > 0)
                initialPos.Z += ApproachHeight;
            goalObjectId = 0;
            goalPosition = initialPos;
            victimId = 0;
        }
        arrived = false;
        prevPos = Owner.Position;
    }

    // ---- Collisions and detonation ----

    public bool HandleCollision(GameObject? other)
    {
        if (!isArmed)
            return true;
        if (other == null)
        {
            // Skimming a hill on the way doesn't count, unless it's near the goal.
            var delta = Owner.Position - goalPosition;
            if (delta.Z > Formats.Maps.MapFile.CellSize && delta.Length() > 3 * Formats.Maps.MapFile.CellSize)
                return true;
        }
        else
        {
            var launcher = Game.FindObject(launcherId);
            if (detonationWeapon != null && !detonationWeapon.ShouldProjectileCollideWith(launcher, Owner, other, victimId))
                return true;
            if (ProjectileHits.TryClearGarrison(this, Owner, launcher, other))
                return true;
        }
        Detonate(other);
        Owner.SetStatus(Status.NoCollisions);
        return true;
    }

    void Detonate(GameObject? directHit = null)
    {
        if (state is MissileState.KillSelf or MissileState.Dead)
            return;
        if (detonationWeapon != null)
        {
            ProjectileHits.Detonate(detonationWeapon, Owner, Game.FindObject(launcherId), directHit, bonus, !noDamage);
            if (detonationWeapon.DieOnDetonate)
                Owner.Damage(new DamageInfo { DamageType = "UNRESISTABLE", DeathType = "DETONATED", Amount = Owner.MaxHealth });
        }
        else if (!noDamage)
            Owner.Damage(new DamageInfo { DamageType = "UNRESISTABLE", DeathType = "DETONATED", Amount = Owner.MaxHealth });
        Owner.DrawableHidden = true;
        SwitchTo(MissileState.KillSelf);
        Owner.SetStatus("MISSILE_KILLING_SELF");
    }

    void AirborneTargetGone()
    {
        fuelExpirationFrame = Frame;
        SwitchTo(MissileState.KillSelf);
    }

    /// <summary>Countermeasure flares will pull the missile off in this many frames (and it then does no damage).</summary>
    public void SetFramesTillCountermeasureDiversion(int frames) => framesTillDecoyed = Frame + frames;

    /// <summary>An ECM jammer scrambles its guidance: it flies off to a random nearby spot instead.</summary>
    public void Jam()
    {
        if (isJammed)
            return;
        isJammed = true;
        Owner.SetCondition("JAMMED");
        var target = isTrackingTarget && GoalObject is { } g ? g.Position : goalPosition;
        target.X += Game.Random.Float(-scatterWhenJammed, scatterWhenJammed);
        target.Y += Game.Random.Float(-scatterWhenJammed, scatterWhenJammed);
        target.Z = Game.Terrain.LayerHeight(target.X, target.Y);
        goalObjectId = 0;
        goalPosition = target;
        arrived = false;
        isTrackingTarget = false;
        originalTargetPos = target;
        victimId = 0;
    }

    // ---- Flying ----

    bool arrived;

    void SetLoco(float accel, float turn)
    {
        if (CurrentLocomotor is { } loco)
        {
            loco.SetMaxAcceleration(accel);
            loco.SetMaxTurnRate(turn);
        }
    }

    void MoveToObject(GameObject target)
    {
        goalObjectId = target.Id;
        goalPosition = target.Position;
        arrived = false;
    }

    void MoveToPosition(Vector3 position)
    {
        goalObjectId = 0;
        goalPosition = position;
        arrived = false;
    }

    protected override bool UpdateDerivedState()
    {
        var pos = Owner.Position;
        if (noTurnDistLeft > 0 && state >= MissileState.Ignition)
        {
            noTurnDistLeft -= Vector3.Distance(pos, prevPos);
            prevPos = pos;
        }

        if (framesTillDecoyed != 0 && framesTillDecoyed <= Frame)
        {
            framesTillDecoyed = 0;
            noDamage = true;
            if (Game.FindObject(victimId) is { } victim && victim.FindModule<CountermeasuresBehavior>()?.FlareToDivertTo(Owner) is { } flare)
            {
                MoveToObject(flare);
                originalTargetPos = flare.Position;
                isTrackingTarget = true;
                victimId = flare.Id;
            }
        }

        if (pos.Z < 0)
        {
            Game.DestroyObject(Owner);
            return true;
        }

        switch (state)
        {
            case MissileState.Prelaunch:
                SetLoco(0, 0);
                break;
            case MissileState.Launch:
                SetLoco(0, 0);
                if (Frame - stateTimestamp >= ignitionDelay)
                {
                    SwitchTo(MissileState.Ignition);
                    DoIgnition();
                }
                break;
            case MissileState.Ignition:
                DoIgnition();
                break;
            case MissileState.AttackNoTurn:
                DoAttack(turnOk: false);
                break;
            case MissileState.Attack:
                DoAttack(turnOk: true);
                break;
            case MissileState.Kill:
                DoKill();
                break;
            case MissileState.KillSelf:
                DoKillSelf();
                break;
            case MissileState.Dead:
                SetLoco(0, 0);
                break;
        }
        if (Owner.IsDestroyed)
            return true;

        // Steer at the goal (the target if it is still there).
        if (state is not (MissileState.KillSelf or MissileState.Dead))
        {
            if (GoalObject is { } goal)
                goalPosition = goal.Position;
            float closeEnough = Math.Max(1, CurrentLocomotor?.CloseEnoughDistance ?? 1);
            if (!arrived && Vector3.Distance(Owner.Position, goalPosition) <= closeEnough)
                arrived = true;
            if (arrived)
                Decelerate();
            else
                SetGoalPositionExplicit(goalPosition);
        }
        else
            Decelerate();
        return true;
    }

    void DoIgnition()
    {
        SetLoco(maxAccel, 0);
        if (ignitionFx.Length > 0)
            Game.Events.PlayFx(ignitionFx, Owner.Position, Owner);
        if (exhaust.Length > 0)
            Game.Events.ParticleSystem(exhaust, Owner.Position, Owner);
        isArmed = true;
        fuelExpirationFrame = fuelLifetime > 0 ? Frame + fuelLifetime : int.MaxValue;
        SwitchTo(MissileState.AttackNoTurn);
    }

    void DoAttack(bool turnOk)
    {
        if (Frame >= fuelExpirationFrame)
        {
            if (detonateOnNoFuel)
            {
                Detonate();
                return;
            }
            SetLoco(0, 0);
        }
        else
            SetLoco(maxAccel, turnOk ? BigNumber : 0);

        if (lockDistance > 0)
        {
            var aim = isTrackingTarget && GoalObject is { } g ? g.Position : goalPosition;
            float distSq = MathUtil.DistanceSquared2D(Owner.Position, aim);
            float lockSq = lockDistance;
            if (!isTrackingTarget)
                lockSq *= 0.5f;
            lockSq *= lockSq;
            if (distSq < lockSq)
            {
                if (!isTrackingTarget)
                    MoveToPosition(originalTargetPos);
                SwitchTo(MissileState.Kill);
                return;
            }
        }

        if (CurrentLocomotor is { PreferredHeight: > 0 } loco
            && MathUtil.DistanceSquared2D(Owner.Position, goalPosition) < diveDistance * diveDistance)
            loco.UsePreciseZPos = true;

        if (noTurnDistLeft <= 0)
            SwitchTo(MissileState.Attack);
        if (isTrackingTarget && GoalObject == null)
            AirborneTargetGone();
    }

    void DoKill()
    {
        if (Frame >= fuelExpirationFrame)
        {
            if (detonateOnNoFuel)
                Detonate();
            else
                AirborneTargetGone();
            return;
        }
        Owner.SetStatus("BRAKING");
        SetLoco(maxAccel, BigNumber);
        if (arrived)
        {
            if (GoalObject is { } goal)
            {
                float closeEnough = CurrentLocomotor?.MaxSpeedFor(Owner) ?? 1;
                float dist = Math.Max(0, Vector3.Distance(Owner.Position, goal.Position) - Owner.BoundingSphereRadius - goal.BoundingSphereRadius);
                if (dist < closeEnough)
                {
                    Owner.SetPosition(goal.Position);
                    Detonate(goal);
                }
                else
                    MoveToObject(goal);
            }
            else
                Detonate();
        }
        if (isTrackingTarget && GoalObject == null)
            AirborneTargetGone();
    }

    void DoKillSelf()
    {
        if (stateTimestamp > Frame - killSelfDelay)
            return;
        if (detonationWeapon != null)
        {
            if (detonateCallsKill)
                Owner.Kill();
            else
                Game.DestroyObject(Owner);
        }
        SwitchTo(MissileState.Dead);
    }
}
