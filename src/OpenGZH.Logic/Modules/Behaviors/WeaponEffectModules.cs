using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Behaviors;

/// <summary>
/// A bunker-buster bomb (the original's BunkerBusterBehavior): when it detonates on its target (once
/// UpgradeRequired is done, if any), whoever is garrisoned or tunnelled inside is hurt and thrown out
/// (OccupantDamageWeaponTemplate) or killed, with DetonationFX and a ShockwaveWeaponTemplate blast.
/// </summary>
public sealed class BunkerBusterBehavior : Module, IUpdateModule, IDieModule
{
    uint victimId;

    public int Update()
    {
        if (Owner.AI is not { } ai)
            return 0;
        if (victimId == 0 && Game.FindObject(ai.TargetId) is { } victim)
            victimId = victim.Id;
        int frequency = Math.Max(1, Definition.Settings.Has("CrashThroughBunkerFXFrequency") ? FramesSetting("CrashThroughBunkerFXFrequency") : 4);
        if (Frame % frequency == 1 && Owner.HasStatus(Flags.ObjectStatus.IndexOf("MISSILE_KILLING_SELF")))
            Game.Events.PlayFx(Setting("CrashThroughBunkerFX"), Owner.Position, Owner);
        return 0;
    }

    public void OnDie(DamageInfo killingBlow) => BustTheBunker();

    void BustTheBunker()
    {
        string upgrade = Setting("UpgradeRequired");
        if (upgrade.Length > 0 && !Owner.Owner.HasUpgrade(upgrade))
            return;
        var target = Game.FindObject(victimId);
        var objectForFx = Owner;
        if (target != null)
        {
            objectForFx = target;
            if (target.Contain is { IsBustable: true } contain)
            {
                if (Game.Catalog.Weapon(Setting("OccupantDamageWeaponTemplate")) is { } weapon)
                    contain.HarmAndForceExitAllContained(new DamageInfo { DamageType = weapon.DamageType, DeathType = weapon.DeathType, SourceId = Owner.Id, Amount = 100 });
                else
                    contain.KillAllContained();
            }
        }
        Game.Events.PlayFx(Setting("DetonationFX"), objectForFx.Position, objectForFx);
        string shockwave = Setting("ShockwaveWeaponTemplate");
        if (shockwave.Length > 0)
            Game.Damage.FireTemporaryWeapon(shockwave, objectForFx, objectForFx.Position);
    }
}

/// <summary>
/// A guided bomb that steers onto the point it was dropped for (the original's SmartBombTargetHomingUpdate):
/// each frame in the air it moves CourseCorrectionScalar of the way... less, keeping that much of its own position.
/// </summary>
public sealed class SmartBombTargetHomingUpdate : Module, IUpdateModule
{
    bool targetReceived;
    Vector3 target;

    public void SetTargetPosition(Vector3 position)
    {
        if (!(position.Length() > 0))
            return;
        target = position;
        targetReceived = true;
    }

    public int Update()
    {
        var self = Owner;
        if (!targetReceived || !self.IsSignificantlyAboveTerrain)
            return 0;
        float statusCoeff = Math.Clamp(FloatSetting("CourseCorrectionScalar", 0.99f), 0, 1);
        float targetCoeff = 1 - statusCoeff;
        var pos = self.Position;
        self.SetPosition(new Vector3(target.X * targetCoeff + pos.X * statusCoeff, target.Y * targetCoeff + pos.Y * statusCoeff, pos.Z));
        return 0;
    }
}

/// <summary>
/// An object whose shape grows from Initial to Final height and radii over TransitionTime after
/// InitialDelay, and shrinks back again with ReverseAtTransitionTime (the original's DynamicGeometryInfoUpdate).
/// </summary>
public class DynamicGeometryInfoUpdate : Module, IUpdateModule, ICreateModule
{
    protected int StartingDelayCountdown { get; private set; }
    int timeActive;
    bool started, finished, reverseAtTransitionTime;
    protected bool SwitchedDirections { get; private set; }
    float initialHeight, initialMajor, initialMinor, finalHeight, finalMajor, finalMinor;

    public void OnCreate()
    {
        StartingDelayCountdown = Math.Max(1, FramesSetting("InitialDelay"));
        reverseAtTransitionTime = BoolSetting("ReverseAtTransitionTime");
        initialHeight = FloatSetting("InitialHeight");
        initialMajor = FloatSetting("InitialMajorRadius");
        initialMinor = FloatSetting("InitialMinorRadius");
        finalHeight = FloatSetting("FinalHeight");
        finalMajor = FloatSetting("FinalMajorRadius");
        finalMinor = FloatSetting("FinalMinorRadius");
    }

    public virtual int Update()
    {
        if (finished)
            return 0;
        if (!started)
        {
            StartingDelayCountdown--;
            if (StartingDelayCountdown > 0)
                return 0;
            started = true;
        }
        var me = Owner;
        int transitionTime = Math.Max(1, Definition.Settings.Has("TransitionTime") ? FramesSetting("TransitionTime") : 1);
        float ratio = timeActive / (float)transitionTime;
        float newHeight = initialHeight + ratio * (finalHeight - initialHeight);
        float newMajor = initialMajor + ratio * (finalMajor - initialMajor);
        float newMinor = initialMinor + ratio * (finalMinor - initialMinor);
        var old = me.Geometry;
        me.SetGeometry(new Geometry(old.Type, newMajor, newMinor, newHeight, old.IsSmall));
        timeActive++;
        if (timeActive > transitionTime)
        {
            if (reverseAtTransitionTime)
            {
                SwitchedDirections = true;
                timeActive = 0;
                reverseAtTransitionTime = false;
                (initialHeight, finalHeight) = (FloatSetting("FinalHeight"), FloatSetting("InitialHeight"));
                (initialMajor, finalMajor) = (FloatSetting("FinalMajorRadius"), FloatSetting("InitialMajorRadius"));
                (initialMinor, finalMinor) = (FloatSetting("FinalMinorRadius"), FloatSetting("InitialMinorRadius"));
            }
            else
                finished = true;
        }
        return 0;
    }
}

/// <summary>
/// The Firestorm (the original's FirestormDynamicGeometryInfoUpdate): a growing column of fire that
/// starts its particle systems once, leaves a scorch when it turns, and burns everything low enough
/// inside it (DamageAmount every DelayBetweenDamageFrames).
/// </summary>
public sealed class FirestormDynamicGeometryInfoUpdate : DynamicGeometryInfoUpdate
{
    bool effectsFired, scorchPlaced;
    int lastDamageFrame;

    public override int Update()
    {
        base.Update();
        if (StartingDelayCountdown > 0)
            return 0;
        var obj = Owner;
        if (!effectsFired)
        {
            var pos = obj.Position with { Z = FloatSetting("ParticleOffsetZ") + Game.Terrain.GroundHeight(obj.Position) };
            for (int i = 1; i <= 16; i++)
                Game.Events.ParticleSystem(Setting($"ParticleSystem{i}"), pos);
            Game.Events.PlayFx(Setting("FXList"), obj.Position, obj);
            effectsFired = true;
        }
        if (SwitchedDirections && !scorchPlaced)
        {
            Game.Effects.Scorch(obj.Position, FloatSetting("ScorchSize"), 1);
            scorchPlaced = true;
        }
        if (Frame - lastDamageFrame >= FloatSetting("DelayBetweenDamageFrames") / LogicTime.MillisecondsPerFrame)
        {
            DoDamageScan();
            lastDamageFrame = Frame;
        }
        return 0;
    }

    void DoDamageScan()
    {
        var firestorm = Owner;
        float boundingCircle = firestorm.Geometry.BoundingCircleRadius;
        if (boundingCircle <= 0)
            return;
        float maxHeight = FloatSetting("MaxHeightForDamage", 20);
        foreach (var other in Game.ObjectsInRadius(firestorm.Position, boundingCircle).ToList())
        {
            if (other.Position.Z > firestorm.Position.Z + maxHeight)
                continue;
            other.Damage(new DamageInfo { DamageType = "FLAME", DeathType = "BURNED", SourceId = firestorm.Id, Amount = FloatSetting("DamageAmount") });
        }
    }
}
