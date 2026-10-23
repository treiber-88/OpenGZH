using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>A dam breaking lets its flood waves go (the original's DamDie): every WAVEGUIDE is enabled.</summary>
public sealed class DamDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        foreach (var obj in Game.Objects.ToList())
            if (obj.IsKindOf("WAVEGUIDE"))
                obj.SetDisabled("DEFAULT", false);
    }
}

/// <summary>
/// A supply dock that stops giving boxes while really damaged and repairs itself SelfHealAmount every
/// SelfHealDelay, once SelfHealSupression has passed since the last hit (the original's
/// SupplyWarehouseCripplingBehavior).
/// </summary>
public sealed class SupplyWarehouseCripplingBehavior : Module, IUpdateModule, IDamageModule, ICreateModule
{
    int healingSupressedUntilFrame, nextHealingFrame;

    public void OnCreate() => Owner.SleepUpdate(this);

    public void OnDamage(DamageInfo damage)
    {
        healingSupressedUntilFrame = Frame + FramesSetting("SelfHealSupression");
        nextHealingFrame = healingSupressedUntilFrame;
        Owner.SleepUpdate(this, Math.Max(0, healingSupressedUntilFrame - Frame));
    }

    public void OnBodyDamageStateChange(BodyDamageState oldState, BodyDamageState newState)
    {
        if (Owner.FindModule<Supply.SupplyWarehouseDockUpdate>() is not { } dock)
            return;
        if (newState == BodyDamageState.ReallyDamaged)
            dock.Crippled = true;
        else if (oldState == BodyDamageState.ReallyDamaged)
            dock.Crippled = false;
    }

    public int Update()
    {
        nextHealingFrame = Frame + FramesSetting("SelfHealDelay");
        Owner.Body?.AttemptHealing(FloatSetting("SelfHealAmount"));
        if (Owner.Body is { } body && body.Health >= body.MaxHealth)
            return LogicTime.SleepForever;
        return Math.Max(0, nextHealingFrame - Frame);
    }
}

/// <summary>
/// A spreading ring that makes the player's KindOf units stealthy (the original's GrantStealthBehavior,
/// the GPS scrambler): it grows from StartRadius by RadiusGrowRate a frame to FinalRadius, then goes.
/// </summary>
public sealed class GrantStealthBehavior : Module, IUpdateModule, ICreateModule
{
    float currentScanRadius;
    FlagSet kindOf;
    uint radiusParticleSystem;

    public void OnCreate()
    {
        currentScanRadius = FloatSetting("StartRadius");
        kindOf = Flags.KindOf.Parse(Setting("KindOf"));
        radiusParticleSystem = Game.Events.AttachParticleSystem(Setting("RadiusParticleSystemName"), Vector3.Zero, Owner);
    }

    public int Update()
    {
        var self = Owner;
        if (self.IsEffectivelyDead)
            return LogicTime.SleepForever;
        currentScanRadius += FloatSetting("RadiusGrowRate");
        bool finalScan = false;
        float finalRadius = FloatSetting("FinalRadius");
        if (currentScanRadius >= finalRadius)
        {
            currentScanRadius = finalRadius;
            finalScan = true;
        }
        foreach (var obj in Game.ObjectsInRadius(self.Position, currentScanRadius).ToList())
        {
            if (obj == self || obj.IsEffectivelyDead || self.RelationshipTo(obj) != Relationship.Allies || obj.IsOffMap != self.IsOffMap)
                continue;
            if (MathUtil.Distance2D(obj.Position, self.Position) > currentScanRadius || !obj.KindOf.HasAny(kindOf))
                continue;
            if (obj.FindModule<Stealth.StealthUpdate>() is { } stealth)
            {
                stealth.ReceiveGrant(true);
                Game.Events.FlashAsSelected(obj);
            }
        }
        if (finalScan)
        {
            Game.Events.StopParticleSystem(radiusParticleSystem);
            Game.DestroyObject(self);
            return LogicTime.SleepForever;
        }
        return 0;
    }
}

/// <summary>
/// A border checkpoint gate (the original's CheckpointUpdate): it opens (DOOR_1_OPENING, narrowing its
/// footprint so traffic can pass) while an ally and no enemy is within sight, and closes otherwise.
/// </summary>
public sealed class CheckpointUpdate : Module, IUpdateModule, ICreateModule
{
    bool allyNear, enemyNear;
    float maxMinorRadius, minorRadius;

    public void OnCreate()
    {
        maxMinorRadius = minorRadius = Owner.Template.GeometryMinorRadius;
    }

    void CheckForAlliesAndEnemies()
    {
        var obj = Owner;
        float range = obj.VisionRange;
        enemyNear = allyNear = false;
        foreach (var other in Game.ObjectsInRadius(obj.Position, range))
        {
            if (other == obj || other.IsEffectivelyDead || other.IsKindOf(Kind.Structure) || MathUtil.Distance2D(other.Position, obj.Position) > range)
                continue;
            var relation = obj.RelationshipTo(other);
            if (relation == Relationship.Enemies && !(other.HasStatus(Status.Stealthed) && !other.HasStatus(Status.Detected)))
                enemyNear = true;
            else if (relation == Relationship.Allies)
                allyNear = true;
        }
    }

    public int Update()
    {
        bool wasAnAlly = allyNear, wasAnEnemy = enemyNear;
        CheckForAlliesAndEnemies();
        bool change = wasAnAlly != allyNear || wasAnEnemy != enemyNear;
        bool open = !enemyNear && allyNear;
        var obj = Owner;
        if (change)
        {
            obj.SetCondition("DOOR_1_CLOSING", !open);
            obj.SetCondition("DOOR_1_OPENING", open);
        }
        if (open)
        {
            if (minorRadius > 0)
                minorRadius -= 0.333f;
        }
        else if (minorRadius < maxMinorRadius)
            minorRadius += 0.333f;
        obj.SetGeometry(obj.Template.Geometry.WithRadii(obj.Template.GeometryMajorRadius, Math.Max(0, minorRadius)));
        return 0;
    }
}
