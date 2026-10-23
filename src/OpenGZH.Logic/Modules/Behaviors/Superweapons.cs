using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.SlowDeath;
using OpenGZH.Logic.Modules.SpecialPowers;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// A superweapon silo's doors (Nuke silo, Scud Storm): they start opening DoorOpenTime before the
/// power is ready, stay open until it is fired, then wait DoorWaitOpenTime and close over
/// DoorCloseTime (at most half the time until the next launch). FX play at each step.
/// </summary>
public sealed class MissileLauncherBuildingUpdate : Module, IUpdateModule, ISpecialPowerUpdate
{
    enum Door { Closed, Opening, Open, WaitingToClose, Closing }

    Door door = Door.Closed;
    Door timeoutState = Door.Closed;
    int timeoutFrame;
    SpecialPowerModule? power;

    SpecialPowerModule? Power => power ??= Owner.FindModules<SpecialPowerModule>()
        .FirstOrDefault(p => p.Power != null && p.Power.Name.Equals(Setting("SpecialPowerTemplate"), StringComparison.OrdinalIgnoreCase));

    void PlayFx(string key)
    {
        if (Setting(key) is { Length: > 0 } fx)
            Game.Events.PlayFx(fx, Owner.Position);
    }

    void SwitchTo(Door next)
    {
        if (door == next)
            return;
        int now = Frame;
        foreach (var c in new[] { "DOOR_1_WAITING_TO_CLOSE", "DOOR_1_CLOSING", "DOOR_1_OPENING", "DOOR_1_WAITING_OPEN" })
            Owner.SetCondition(c, false);
        switch (next)
        {
            case Door.Closed:
                timeoutFrame = 0;
                timeoutState = Door.Closed;
                PlayFx("DoorClosedFX");
                break;
            case Door.Opening:
                Owner.SetCondition("DOOR_1_OPENING");
                timeoutFrame = (Power?.ReadyFrame ?? now) - 1;
                timeoutState = Door.Open;
                PlayFx("DoorOpeningFX");
                break;
            case Door.Open:
                Owner.SetCondition("DOOR_1_WAITING_OPEN");
                timeoutFrame = 0;
                timeoutState = Door.Open;
                PlayFx("DoorOpenFX");
                if (Setting("DoorOpenIdleAudio") is { Length: > 0 } idle)
                    Game.Events.PlaySound(idle, Owner.Position, source: Owner);
                break;
            case Door.WaitingToClose:
                Owner.SetCondition("DOOR_1_WAITING_TO_CLOSE");
                timeoutFrame = now + FramesSetting("DoorWaitOpenTime");
                timeoutState = Door.Closing;
                PlayFx("DoorWaitingToCloseFX");
                break;
            case Door.Closing:
                Owner.SetCondition("DOOR_1_CLOSING");
                timeoutFrame = now + FramesSetting("DoorCloseTime");
                int delta = (Power?.ReadyFrame ?? now) - now;
                if (timeoutFrame > now + delta / 2)
                    timeoutFrame = now + delta / 2;
                timeoutState = Door.Closed;
                PlayFx("DoorClosingFX");
                break;
        }
        door = next;
    }

    public bool InitiateIntentToDoSpecialPower(SpecialPowerTemplate template, GameObject? target, Vector3? position, uint options)
    {
        if (Power?.Power != template)
            return false;
        SwitchTo(Door.WaitingToClose);
        return true;
    }

    public int Update()
    {
        if (Owner.IsUnderConstruction || Power is not { } p)
            return 0;
        int now = Frame;
        int readyFrame = p.ReadyFrame;
        int openTime = FramesSetting("DoorOpenTime");
        int startOpening = readyFrame >= openTime ? readyFrame - openTime : 0;
        if (timeoutFrame != 0 && now > timeoutFrame)
            SwitchTo(timeoutState);
        if (door != Door.Open && p.IsReady)
            SwitchTo(Door.Open);
        else if (door == Door.Closed && now >= startOpening)
            SwitchTo(Door.Opening);
        return 0;
    }
}

/// <summary>
/// The Nuke: rises out of its silo (SpecialSpeedTime/Height/AccelFactor, shaking by
/// SpecialJitterDistance), flies DistanceToTravelBeforeTurning straight, then turns (MaxTurnRate)
/// towards a point TargetFromDirectlyAbove over the target and drops straight down on it at half
/// speed. RelativeSpeed and ForwardDamping set its thrust. It goes off on touching anything but its
/// silo, or the ground.
/// </summary>
public sealed class NeutronMissileUpdate : Module, IUpdateModule, IProjectile, ICreateModule
{
    const float StraightDownSlowFactor = 0.5f;

    enum MissileState { Prelaunch, Launch, Attack, Dead }

    float initialDist, maxTurnRate, forwardDamping, relativeSpeed, fromAbove;
    float specialSpeedHeight, specialAccelFactor, specialJitter;
    int specialSpeedTime;

    MissileState state = MissileState.Prelaunch;
    float noTurnDistLeft;
    bool reachedIntermediate = true, isArmed, isLaunched;
    Vector3 targetPos, intermediatePos, velocity, direction = Vector3.UnitZ;
    uint launcherId;
    float heightAtLaunch;
    int frameAtLaunch;
    string exhaust = "";

    protected override void Initialize()
    {
        initialDist = FloatSetting("DistanceToTravelBeforeTurning");
        maxTurnRate = Definition.Settings.Has("MaxTurnRate") ? RuleUnits.AngularVelocity(Setting("MaxTurnRate")) : 999;
        forwardDamping = FloatSetting("ForwardDamping");
        relativeSpeed = FloatSetting("RelativeSpeed", 1);
        fromAbove = FloatSetting("TargetFromDirectlyAbove");
        specialSpeedTime = FramesSetting("SpecialSpeedTime");
        specialSpeedHeight = FloatSetting("SpecialSpeedHeight");
        specialAccelFactor = FloatSetting("SpecialAccelFactor", 1);
        specialJitter = FloatSetting("SpecialJitterDistance");
        noTurnDistLeft = initialDist;
    }

    public void OnCreate()
    {
        if (Owner.FindModule<PhysicsBehavior>() is { } physics)
            physics.MovedByOwnModule = true;
    }

    public bool IsArmed => isArmed;

    public void Launch(GameObject launcher, WeaponTemplate weapon, GameObject? target, Vector3 position, WeaponBonus bonus)
    {
        launcherId = launcher.Id;
        exhaust = weapon.ProjectileExhaust[(int)launcher.Veterancy];
        velocity = launcher.FindModule<PhysicsBehavior>()?.Velocity ?? Vector3.Zero;
        state = MissileState.Launch;
        targetPos = target?.Position ?? position;
        intermediatePos = targetPos + new Vector3(0, 0, fromAbove);
        // The target circle stays until the missile is gone.
        Game.Effects.CreateRadiusDecal(Effects.RadiusDecalTemplate.Parse(Definition.Settings.TryGet("DeliveryDecal", out var decal) ? decal : null),
            targetPos, FloatSetting("DeliveryDecalRadius"), Owner.Owner, Owner);
    }

    public bool HandleCollision(GameObject? other)
    {
        if (!isArmed)
            return true;
        if (other != null && other.Id == launcherId)
            return true;
        Detonate();
        Owner.SetStatus(Status.NoCollisions);
        return true;
    }

    void Detonate()
    {
        Owner.Kill();
        state = MissileState.Dead;
        Owner.DrawableHidden = true;
    }

    void DoLaunch()
    {
        if (!isLaunched)
        {
            if (Game.FindObject(launcherId) is not { } launcher)
            {
                Game.DestroyObject(Owner);
                return;
            }
            // Out of the silo's launch point, pointing up.
            var start = Game.Bones.TryGetBone(launcher, "WeaponA01", out var bone) ? bone.Translation
                : launcher.Position + new Vector3(0, 0, launcher.Height * 0.5f);
            Owner.DrawableHidden = false;
            Owner.SetPosition(start);
            direction = Vector3.UnitZ;
            Orient();
            Owner.ExperienceSinkId = launcherId;
            isLaunched = true;
            if (fromAbove != 0)
                reachedIntermediate = false;
            if (Setting("LaunchFX") is { Length: > 0 } launchFx)
                Game.Events.PlayFx(launchFx, Owner.Position, Owner);
            heightAtLaunch = Owner.Position.Z;
            frameAtLaunch = Frame;
        }
        Owner.SetPosition(Owner.Position + velocity);
        if (Setting("IgnitionFX") is { Length: > 0 } ignition)
            Game.Events.PlayFx(ignition, Owner.Position, Owner);
        if (exhaust.Length > 0)
            Game.Events.ParticleSystem(exhaust, Owner.Position, Owner);
        state = MissileState.Attack;
        isArmed = true;
    }

    void Orient()
    {
        Owner.SetOrientation(MathF.Atan2(direction.Y, direction.X));
        Owner.Pitch = MathF.Asin(Math.Clamp(direction.Z, -1, 1));
    }

    void DoAttack()
    {
        float speed = relativeSpeed;
        if (fromAbove != 0 && reachedIntermediate)
            speed *= StraightDownSlowFactor;
        if (noTurnDistLeft <= 0)
        {
            // Turn towards the goal by at most MaxTurnRate.
            var goal = reachedIntermediate ? targetPos : intermediatePos;
            var toGoal = goal - Owner.Position;
            if (toGoal.LengthSquared() > 0)
            {
                toGoal = Vector3.Normalize(toGoal);
                float angle = MathF.Acos(Math.Clamp(Vector3.Dot(direction, toGoal), -1, 1));
                if (MathF.Abs(angle) < maxTurnRate)
                    direction = toGoal;
                else
                {
                    var axis = Vector3.Cross(direction, toGoal);
                    if (axis.LengthSquared() > 1e-8f)
                        direction = Vector3.Normalize(Vector3.Transform(direction, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), maxTurnRate)));
                }
            }
        }
        var accel = speed * direction - forwardDamping * velocity;
        velocity += accel;

        var pos = Owner.Position;
        int now = Frame;
        if (specialSpeedTime > 0 && now <= frameAtLaunch + specialSpeedTime)
        {
            Owner.DrawJitter = Vector3.Zero;
            int elapsed = now - frameAtLaunch;
            if (elapsed < specialSpeedTime)
            {
                float timeFrac = (float)elapsed / specialSpeedTime;
                float accelFactor = Math.Max(0.01f, specialAccelFactor);
                var newPos = pos with { Z = heightAtLaunch + accelFactor * timeFrac * (accelFactor * timeFrac) / accelFactor * specialSpeedHeight };
                velocity = newPos - pos;
                if (specialJitter > 0)
                {
                    float amplitude = (1 - timeFrac) * specialJitter;
                    var side = Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ));
                    var up = Vector3.Cross(side, direction);
                    Owner.DrawJitter = (side * Game.Random.Float(-1, 1) + up * Game.Random.Float(-1, 1)) * amplitude;
                }
            }
        }
        Orient();
        Owner.SetPosition(pos + velocity);
    }

    public int Update()
    {
        if (!reachedIntermediate && Vector3.DistanceSquared(Owner.Position, intermediatePos) <= Owner.BoundingSphereRadius * Owner.BoundingSphereRadius)
        {
            reachedIntermediate = true;
            Owner.SetPosition(intermediatePos);
            float speed = velocity.Length();
            velocity = new Vector3(0, 0, -speed * StraightDownSlowFactor);
            direction = -Vector3.UnitZ;
        }
        var oldPos = Owner.Position;
        bool oldValid = state == MissileState.Attack;
        switch (state)
        {
            case MissileState.Launch: DoLaunch(); break;
            case MissileState.Attack: DoAttack(); break;
        }
        if (Owner.IsDestroyed)
            return 0;
        if (noTurnDistLeft > 0 && oldValid)
            noTurnDistLeft -= Vector3.Distance(Owner.Position, oldPos);
        if (state is MissileState.Launch or MissileState.Attack && !Owner.IsAboveTerrain)
            HandleCollision(null);
        return 0;
    }
}

/// <summary>
/// The Nuke's death: FXList at ground zero, then up to nine blast waves (BlastN...), each at its
/// own Delay hurting everything within OuterRadius (MaxDamage inside InnerRadius, falling to
/// MinDamage at the edge) and toppling trees outwards (ToppleSpeed), and at ScorchDelay leaving
/// everything in it BURNED.
/// </summary>
public sealed class NeutronMissileSlowDeathBehavior : SlowDeathBehavior
{
    const int MaxBlasts = 9;

    sealed class Blast
    {
        public bool Enabled;
        public float Delay, ScorchDelay, InnerRadius, OuterRadius, MaxDamage, MinDamage, ToppleSpeed, PushForce;
    }

    readonly Blast[] blasts = new Blast[MaxBlasts];
    readonly bool[] completed = new bool[MaxBlasts], completedScorch = new bool[MaxBlasts];
    int activationFrame;

    protected override void InitializeDie()
    {
        base.InitializeDie();
        for (int i = 0; i < MaxBlasts; i++)
        {
            string p = $"Blast{i + 1}";
            blasts[i] = new Blast
            {
                Enabled = BoolSetting(p + "Enabled"),
                Delay = LogicTime.Frames(FloatSetting(p + "Delay")),
                ScorchDelay = LogicTime.Frames(FloatSetting(p + "ScorchDelay")),
                InnerRadius = FloatSetting(p + "InnerRadius"),
                OuterRadius = FloatSetting(p + "OuterRadius"),
                MaxDamage = FloatSetting(p + "MaxDamage"),
                MinDamage = FloatSetting(p + "MinDamage"),
                ToppleSpeed = FloatSetting(p + "ToppleSpeed"),
                PushForce = FloatSetting(p + "PushForce"),
            };
        }
    }

    public override int Update()
    {
        base.Update();
        if (!IsActive)
            return 0;
        int now = Frame;
        if (activationFrame == 0)
        {
            activationFrame = now;
            if (Setting("FXList") is { Length: > 0 } fx)
                Game.Events.PlayFx(fx, Owner.Position with { Z = Game.Terrain.GroundHeight(Owner.Position) });
        }
        for (int i = 0; i < MaxBlasts; i++)
        {
            var b = blasts[i];
            if (!b.Enabled)
                continue;
            if (!completed[i] && now - activationFrame > b.Delay)
            {
                DoBlast(b);
                completed[i] = true;
            }
            if (!completedScorch[i] && now - activationFrame > b.ScorchDelay)
            {
                foreach (var other in Game.Partition.Query(Owner.Position, b.OuterRadius).ToList())
                    if (MathUtil.Distance2D(other.Position, Owner.Position) <= b.OuterRadius)
                        other.SetCondition("BURNED");
                completedScorch[i] = true;
            }
        }
        return 0;
    }

    void DoBlast(Blast b)
    {
        if (b.OuterRadius <= 0)
            return;
        var center = Owner.Position;
        foreach (var other in Game.Partition.Query(center, b.OuterRadius).ToList())
        {
            if (other == Owner || MathUtil.Distance2D(other.Position, center) > b.OuterRadius)
                continue;
            var force = other.Position - center;
            other.FindModule<ToppleUpdate>()?.Topple(force, b.ToppleSpeed, ToppleUpdate.Options.NoBounce | ToppleUpdate.Options.NoFx);
            float dist = force.Length();
            float amount = dist <= b.InnerRadius ? b.MaxDamage
                : Math.Max(b.MinDamage, b.MaxDamage * (1 - (dist - b.InnerRadius) / (b.OuterRadius - b.InnerRadius + 0.01f)));
            if (amount != 0)
                other.Damage(new DamageInfo { DamageType = "EXPLOSION", DeathType = "EXPLODED", SourceId = Owner.Id, Amount = amount });
        }
    }
}

/// <summary>
/// China's neutron shells and mines: on death, every infantryman within BlastRadius dies, every
/// container's occupants die, and vehicles are left unmanned and neutral (Combat Cycles die).
/// Not aircraft unless AffectAirborne; not allies unless AffectAllies.
/// </summary>
public sealed class NeutronBlastBehavior : Module, IDieModule
{
    public void OnDie(DamageInfo killingBlow)
    {
        float radius = FloatSetting("BlastRadius");
        bool hitAir = BoolSetting("AffectAirborne");
        bool affectAllies = BoolSetting("AffectAllies", true);
        foreach (var obj in Game.Partition.Query(Owner.Position, radius).ToList())
        {
            if (obj == Owner || obj.IsEffectivelyDead || MathUtil.Distance2D(obj.Position, Owner.Position) > radius)
                continue;
            if (!hitAir && (obj.IsKindOf("AIRCRAFT") || obj.IsAirborneTarget))
                continue;
            if (!affectAllies && Owner.Owner.IsAllyOf(obj.Owner))
                continue;
            if (obj.IsKindOf(Kind.Infantry))
                obj.Kill();
            obj.Contain?.KillAllContained();
            if (obj.IsKindOf("VEHICLE") && !obj.IsKindOf("DRONE"))
            {
                if (obj.IsKindOf("CLIFF_JUMPER"))
                    obj.Kill();
                else
                {
                    obj.SetDisabled("DISABLED_UNMANNED");
                    obj.AI?.Stop(Commands.CommandSource.AI);
                    obj.SetTeam(Game.Players.Neutral.DefaultTeam);
                }
            }
        }
    }
}

/// <summary>
/// Trees, poles and lamp posts that fall over when a heavy vehicle (crusher level above 1) runs into
/// them or a blast pushes them: they fall away from the push (sideways only with
/// ToppleLeftOrRightOnly), bounce (BounceVelocityPercent, BounceFX) and die when down
/// (KillWhenFinishedToppling), leaving StumpName behind.
/// </summary>
public sealed class ToppleUpdate : Module, IUpdateModule, ICollideModule, ICreateModule
{
    [Flags]
    public enum Options { None = 0, NoBounce = 1, NoFx = 2 }

    const float AngularLimit = MathF.PI / 2 - MathF.PI / 64;

    enum ToppleState { Upright, Falling, Down }

    ToppleState state;
    Options options;
    float angularVelocity, angularAccumulation, angularAcceleration;
    float angleDeltaX;
    int numAngleDeltaX;
    Vector3 toppleDirection;
    uint stumpId;

    public void OnCreate() => Owner.SleepUpdate(this);

    public bool IsAbleToBeToppled => state == ToppleState.Upright;

    static float AngleClosestTo(float a1, float a2, float desired)
    {
        a1 = MathUtil.NormalizeAngle(a1);
        a2 = MathUtil.NormalizeAngle(a2);
        return MathF.Abs(MathUtil.AngleDifference(desired, a1)) < MathF.Abs(MathUtil.AngleDifference(desired, a2)) ? a1 : a2;
    }

    public void Topple(Vector3 direction, float speed, Options how)
    {
        if (!IsAbleToBeToppled || Owner.IsEffectivelyDead)
            return;
        Owner.WakeUpdate(this);
        if (BoolSetting("KillWhenStartToppling"))
        {
            Owner.SleepUpdate(this);
            Owner.Kill();
            return;
        }
        direction.Z = 0;
        toppleDirection = direction.LengthSquared() > 0 ? Vector3.Normalize(direction) : Vector3.UnitX;
        angularVelocity = speed * FloatSetting("InitialVelocityPercent", 0.2f);
        angularAcceleration = speed * FloatSetting("InitialAccelPercent", 0.01f);
        state = ToppleState.Falling;
        options = how;

        float current = MathUtil.NormalizeAngle(Owner.Orientation);
        float toppleAngle = MathUtil.NormalizeAngle(MathF.Atan2(toppleDirection.Y, toppleDirection.X));
        if (BoolSetting("ToppleLeftOrRightOnly"))
        {
            toppleAngle = AngleClosestTo(current + MathF.PI / 2, current - MathF.PI / 2, toppleAngle);
            toppleDirection = new Vector3(MathF.Cos(toppleAngle), MathF.Sin(toppleAngle), 0);
        }
        float desired = AngleClosestTo(toppleAngle + MathF.PI / 2, toppleAngle - MathF.PI / 2, current);
        numAngleDeltaX = Math.Max(1, (int)MathF.Floor(AngularLimit / Math.Max(0.0001f, angularVelocity * 2)));
        angleDeltaX = MathUtil.AngleDifference(desired, current) / numAngleDeltaX;
        Owner.SetCondition("TOPPLED");
        if (Setting("ToppleFX") is { Length: > 0 } fx)
            Game.Events.PlayFx(fx, Owner.Position, Owner);
        if (Setting("StumpName") is { Length: > 0 } stumpName && Game.CreateObject(stumpName, Game.Players.Neutral, Owner.Position, Owner.Orientation) is { } stump)
        {
            stumpId = stump.Id;
            if (Owner.HasCondition("BURNED"))
                stump.SetCondition("BURNED");
        }
    }

    static void DeathByToppling(GameObject obj) =>
        obj.Damage(new DamageInfo { DamageType = "UNRESISTABLE", DeathType = "TOPPLED", Amount = DamageInfo.HugeDamage });

    public int Update()
    {
        if (state != ToppleState.Falling)
            return LogicTime.SleepForever;
        if (numAngleDeltaX > 0)
        {
            Owner.SetOrientation(Owner.Orientation + angleDeltaX);
            numAngleDeltaX--;
        }
        float step = angularVelocity;
        if (angularAccumulation + step > AngularLimit)
            step = AngularLimit - angularAccumulation;
        angularAccumulation += step;
        // Lean over towards the topple direction.
        Owner.Tilt = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(-toppleDirection.Y, toppleDirection.X, 0)), angularAccumulation);

        if (angularAccumulation >= AngularLimit && angularVelocity > 0)
        {
            angularVelocity *= -FloatSetting("BounceVelocityPercent", 0.3f);
            if (options.HasFlag(Options.NoBounce) || MathF.Abs(angularVelocity) < 0.01f)
            {
                angularVelocity = 0;
                state = ToppleState.Down;
                if (BoolSetting("KillWhenFinishedToppling", true))
                    DeathByToppling(Owner);
                if (BoolSetting("KillStumpWhenToppled") && Game.FindObject(stumpId) is { } stump)
                    DeathByToppling(stump);
            }
            else if (MathF.Abs(angularVelocity) >= 0.03f && !options.HasFlag(Options.NoFx) && Setting("BounceFX") is { Length: > 0 } bounce)
                Game.Events.PlayFx(bounce, Owner.Position, Owner);
        }
        else
            angularVelocity += angularAcceleration;
        return 0;
    }

    public void OnCollide(GameObject? other, Vector3 location, Vector3 normal)
    {
        if (other == null || other.Template.CrusherLevel <= 1)
            return;
        var away = Owner.Position - other.Position;
        away.Z = 0;
        float speed = other.FindModule<PhysicsBehavior>()?.Velocity.Length() ?? 0;
        Topple(away, speed, Options.None);
    }
}
