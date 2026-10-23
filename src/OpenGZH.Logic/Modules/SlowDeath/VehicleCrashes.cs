using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.SlowDeath;

/// <summary>
/// A jet shot down (the original's JetSlowDeathBehavior): on the ground (or on a carrier deck) it
/// just blows up; in the air it rolls (RollRate, multiplied by RollRateDelta each frame), falls
/// (FallHowFast of gravity), plays its secondary explosion after DelaySecondaryFromInitialDeath,
/// pitches over when it hits the ground or a tree, and blows up DelayFinalBlowUpFromHitGround later.
/// </summary>
public sealed class JetSlowDeathBehavior : SlowDeathBehavior
{
    int timerDeathFrame, timerOnGroundFrame;
    float rollRate;
    uint deathLoopSound;

    static readonly int DeckHeightOffset = Flags.ObjectStatus.IndexOf("DECK_HEIGHT_OFFSET");

    protected override void Die(DamageInfo killingBlow)
    {
        var us = Owner;
        if (!us.IsSignificantlyAboveTerrain || us.HasStatus(DeckHeightOffset))
        {
            Game.Events.PlayFx(Setting("FXOnGroundDeath"), us.Position, us);
            Game.ObjectCreation.Run(Setting("OCLOnGroundDeath"), us, us.Position);
            Game.DestroyObject(us);
        }
        else
            base.Die(killingBlow);
        us.SetStatus(DeckHeightOffset, false);
    }

    public override void BeginSlowDeath(DamageInfo killingBlow)
    {
        base.BeginSlowDeath(killingBlow);
        var us = Owner;
        timerDeathFrame = Frame;
        Game.Events.PlayFx(Setting("FXInitialDeath"), us.Position, us);
        Game.ObjectCreation.Run(Setting("OCLInitialDeath"), us, us.Position);
        deathLoopSound = Game.Events.PlayHandledSound(Setting("DeathLoopSound"), us);
        rollRate = FloatSetting("RollRate");
        if (us.AI?.CurrentLocomotor is { } locomotor)
        {
            locomotor.SetMaxLift(-Game.Catalog.GameData.Gravity * (1 - FloatSetting("FallHowFast")));
            locomotor.SetMaxTurnRate(0);
        }
    }

    public override int Update()
    {
        base.Update();
        if (!IsSlowDeathActivated)
            return 0;
        var us = Owner;
        var physics = us.FindModule<PhysicsBehavior>();
        if (physics != null)
            physics.RollRate = rollRate;
        rollRate *= FloatSetting("RollRateDelta", 1);

        if (timerOnGroundFrame == 0)
        {
            float height = us.HeightAboveTerrain;
            if (Game.Terrain.LayerHeight(us.Position.X, us.Position.Y) > Game.Terrain.GroundHeight(us.Position) && height >= 0 && height <= 1)
                height = 0; // landing on a bridge
            bool hitATree = physics != null && Game.FindObject(physics.LastCollideeId) is { } tree && tree.IsKindOf(Kind.Shrubbery);
            if (height <= 0 || hitATree)
            {
                Game.Events.StopHandledSound(deathLoopSound);
                Game.Events.PlayFx(Setting("FXHitGround"), us.Position, us);
                Game.ObjectCreation.Run(Setting("OCLHitGround"), us, us.Position);
                timerOnGroundFrame = Frame;
                if (physics != null)
                    physics.PitchRate = FloatSetting("PitchRate");
            }
            if (timerDeathFrame != 0 && Frame - timerDeathFrame >= FramesSetting("DelaySecondaryFromInitialDeath"))
            {
                Game.Events.PlayFx(Setting("FXSecondary"), us.Position, us);
                Game.ObjectCreation.Run(Setting("OCLSecondary"), us, us.Position);
                timerDeathFrame = 0;
            }
        }
        else if (Frame - timerOnGroundFrame >= FramesSetting("DelayFinalBlowUpFromHitGround"))
        {
            Game.Events.PlayFx(Setting("FXFinalBlowUp"), us.Position, us);
            Game.ObjectCreation.Run(Setting("OCLFinalBlowUp"), us, us.Position);
            Game.DestroyObject(us);
        }
        return 0;
    }
}

/// <summary>
/// A helicopter going down in a spiral (the original's HelicopterSlowDeathBehavior): it spins on
/// itself between MinSelfSpin and MaxSelfSpin, orbits with SpiralOrbitTurnRate and a damped forward
/// push, loses its blades (BladeObjectName / OCLBlade at BladeBoneName) after a random delay, crashes,
/// and DelayFromGroundToFinalDeath later blows up and leaves FinalRubbleObject.
/// </summary>
public sealed class HelicopterSlowDeathBehavior : SlowDeathBehavior
{
    const float OrbitDirectionLeft = 1;

    float orbitDirection = OrbitDirectionLeft;
    float forwardAngle, forwardSpeed, selfSpin;
    bool selfSpinTowardsMax;
    int lastSelfSpinUpdateFrame;
    float bladeFlyOffFrame;
    int hitGroundFrame;
    uint deathSound;

    float DurationRealFrames(string key) => FloatSetting(key) / LogicTime.MillisecondsPerFrame;

    public override void BeginSlowDeath(DamageInfo killingBlow)
    {
        base.BeginSlowDeath(killingBlow);
        var copter = Owner;
        deathSound = Game.Events.PlayHandledSound(Setting("SoundDeathLoop"), copter);
        bladeFlyOffFrame = Game.Random.Float(DurationRealFrames("MinBladeFlyOffDelay"), DurationRealFrames("MaxBladeFlyOffDelay"));
        orbitDirection = OrbitDirectionLeft;
        forwardAngle = copter.Orientation;
        forwardSpeed = RuleUnits.Velocity(Setting("SpiralOrbitForwardSpeed"));
        selfSpin = RuleUnits.AngularVelocity(Setting("MinSelfSpin"));
        selfSpinTowardsMax = true;
        if (copter.AI is not { } ai)
            return;
        if (ai.CurrentLocomotor is { } locomotor)
        {
            locomotor.SetMaxLift(-Game.Catalog.GameData.Gravity * (1 - FloatSetting("FallHowFast")));
            locomotor.SetMaxBraking(Definition.Settings.Has("MaxBraking") ? RuleUnits.Acceleration(Setting("MaxBraking")) : 99999);
        }
        string particle = Setting("AttachParticle");
        if (particle.Length > 0)
        {
            string bone = Setting("AttachParticleBone");
            var local = OpenGZH.Core.Rules.RuleValue.Coord(Setting("AttachParticleLoc"));
            if (bone.Length > 0)
                local = Game.Bones.TryGetLocalBone(copter, bone, out var pos) ? pos : Vector3.Zero;
            Game.Events.AttachParticleSystem(particle, local, copter);
        }
    }

    public override int Update()
    {
        base.Update();
        if (!IsSlowDeathActivated)
            return 0;
        var copter = Owner;
        var physics = copter.FindModule<PhysicsBehavior>();
        if (hitGroundFrame == 0)
        {
            copter.SetOrientation(copter.Orientation + selfSpin * orbitDirection);
            float spinDelay = DurationRealFrames("SelfSpinUpdateDelay");
            if (spinDelay != 0 && Frame - lastSelfSpinUpdateFrame > spinDelay)
            {
                float amount = RuleUnits.Angle(Setting("SelfSpinUpdateAmount")) / LogicTime.FramesPerSecond;
                float min = RuleUnits.AngularVelocity(Setting("MinSelfSpin")), max = RuleUnits.AngularVelocity(Setting("MaxSelfSpin"));
                if (selfSpinTowardsMax)
                {
                    selfSpin += amount;
                    if (selfSpin > max)
                    {
                        selfSpin = max;
                        selfSpinTowardsMax = false;
                    }
                }
                else
                {
                    selfSpin -= amount;
                    if (selfSpin < min)
                    {
                        selfSpin = min;
                        selfSpinTowardsMax = true;
                    }
                }
                lastSelfSpinUpdateFrame = Frame;
            }
            physics?.ApplyMotiveForce(new Vector3(MathF.Cos(forwardAngle) * forwardSpeed, MathF.Sin(forwardAngle) * forwardSpeed, 0));
            forwardAngle += RuleUnits.AngularVelocity(Setting("SpiralOrbitTurnRate")) * orbitDirection;
            forwardSpeed *= FloatSetting("SpiralOrbitForwardSpeedDamping", 1);

            if (bladeFlyOffFrame > 0)
            {
                bladeFlyOffFrame--;
                if (bladeFlyOffFrame <= 0)
                {
                    var bladePos = copter.Position;
                    if (Game.Bones.TryGetLocalBone(copter, Setting("BladeBoneName"), out var local))
                        bladePos = Art.ModelBones.ToWorld(copter, local);
                    // (Without the bone the original converts the copter's own position as if it were one.)
                    Game.Events.PlayFx(Setting("FXBlade"), bladePos);
                    Game.ObjectCreation.Run(Setting("OCLBlade"), copter, bladePos);
                    if (Setting("OCLEjectPilot").Length > 0 && copter.Veterancy > VeterancyLevel.Regular)
                        Modules.Die.EjectPilotDie.EjectPilot(Setting("OCLEjectPilot"), copter, null);
                }
            }
        }

        bool hitATree = hitGroundFrame == 0 && physics != null && Game.FindObject(physics.LastCollideeId) is { } tree && tree.IsKindOf(Kind.Shrubbery);
        if (hitGroundFrame == 0)
        {
            float ground = Game.Terrain.HighestLayerHeight(copter.Position.X, copter.Position.Y);
            if (copter.Position.Z <= ground + 1 || hitATree)
            {
                hitGroundFrame = Frame;
                Game.Events.PlayFx(Setting("FXHitGround"), copter.Position, copter);
                Game.ObjectCreation.Run(Setting("OCLHitGround"), copter, copter.Position);
                copter.SetDisabled("DISABLED_HELD");
                copter.SetCondition("SPECIAL_DAMAGED");
                Game.Events.StopHandledSound(deathSound);
            }
        }
        if (hitGroundFrame != 0 && Frame - hitGroundFrame > DurationRealFrames("DelayFromGroundToFinalDeath"))
        {
            Game.Events.PlayFx(Setting("FXFinalBlowUp"), copter.Position, copter);
            Game.ObjectCreation.Run(Setting("OCLFinalBlowUp"), copter, copter.Position);
            if (Game.Templates.Find(Setting("FinalRubbleObject")) is { } rubbleTemplate)
            {
                var rubble = Game.CreateObject(rubbleTemplate, copter.Owner, copter.Position, copter.Orientation, copter.Team);
                rubble.Pitch = copter.Pitch;
                rubble.Roll = copter.Roll;
            }
            Game.DestroyObject(copter);
        }
        return 0;
    }
}

/// <summary>
/// The GLA battle bus (the original's BattleBusSlowDeathBehavior): its first "death" (begun by its
/// UndeadBody, not by dying) throws it into the air and hurts the passengers; landed, it becomes a
/// SECOND_LIFE hulk that the passengers can keep fighting from, and dies once left empty for
/// EmptyHulkDestructionDelay. The real death is an ordinary slow death.
/// </summary>
public sealed class BattleBusSlowDeathBehavior : SlowDeathBehavior
{
    const int GroundCheckDelay = 10;
    const int EmptyHulkCheckDelay = 15;

    bool isRealDeath, isInFirstDeath;
    int groundCheckFrame, penaltyDeathFrame;

    protected override void Die(DamageInfo killingBlow)
    {
        isRealDeath = true;
        isInFirstDeath = false;
        base.Die(killingBlow);
    }

    public override void BeginSlowDeath(DamageInfo killingBlow)
    {
        var me = Owner;
        if (!isRealDeath)
        {
            isInFirstDeath = true;
            groundCheckFrame = Frame + GroundCheckDelay;
            Game.Events.PlayFx(Setting("FXStartUndeath"), me.Position, me);
            Game.ObjectCreation.Run(Setting("OCLStartUndeath"), me, me.Position);
            me.AI?.Stop(Commands.CommandSource.AI);
            if (me.FindModule<PhysicsBehavior>() is { } physics)
            {
                physics.ClearAcceleration();
                physics.ScrubVelocity2D(0);
                physics.ApplyShock(new Vector3(0, 0, FloatSetting("ThrowForce", 1)));
                physics.ApplyRandomRotation();
            }
            me.Contain?.ProcessDamageToContained(FloatSetting("PercentDamageToPassengers"));
            me.WakeUpdate(this);
        }
        else
            base.BeginSlowDeath(killingBlow);
    }

    public override int Update()
    {
        var me = Owner;
        if (isInFirstDeath)
        {
            if (groundCheckFrame < Frame && !me.IsAboveTerrain)
            {
                isInFirstDeath = false;
                Game.Events.PlayFx(Setting("FXHitGround"), me.Position, me);
                Game.ObjectCreation.Run(Setting("OCLHitGround"), me, me.Position);
                me.SetCondition("SECOND_LIFE");
                me.AI?.Stop(Commands.CommandSource.AI);
                if (me.FindModule<PhysicsBehavior>() is { } physics)
                {
                    physics.ClearAcceleration();
                    physics.ScrubVelocity2D(0);
                }
                me.SetDisabled("DISABLED_HELD");
                return FramesSetting("EmptyHulkDestructionDelay") == 0 ? LogicTime.SleepForever : 0;
            }
            return 0;
        }
        if (isRealDeath)
            return base.Update();

        // A hulk: it goes when nobody is left inside.
        if (me.Contain is not { } contain)
            return LogicTime.SleepForever;
        if (penaltyDeathFrame != 0)
        {
            if (contain.ContainCount > 0)
            {
                penaltyDeathFrame = 0;
                return EmptyHulkCheckDelay;
            }
            if (Frame > penaltyDeathFrame)
            {
                me.Kill("EXTRA_4");
                return LogicTime.SleepForever;
            }
            return EmptyHulkCheckDelay;
        }
        if (contain.ContainCount == 0)
            penaltyDeathFrame = Frame + FramesSetting("EmptyHulkDestructionDelay");
        return EmptyHulkCheckDelay;
    }
}
