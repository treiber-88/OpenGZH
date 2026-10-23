using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.SlowDeath;

/// <summary>
/// A death that takes time (the original's SlowDeathBehavior): the object plays its dying animation,
/// may be flung into the air, sinks into the ground after SinkDelay, and is removed after
/// DestructionDelay. Effects run at three moments: INITIAL (at death), MIDPOINT (somewhere between
/// 35% and 65% of the way) and FINAL (when removed).
///
/// An object may have several slow deaths; when it dies, one applicable to the killing blow is picked
/// at random, weighted by ProbabilityModifier (heavy overkill adds ModifierBonusPerOverkillPercent).
/// Jets, helicopters and the battle bus extend this with their own crashes.
/// </summary>
public class SlowDeathBehavior : DieModule, IUpdateModule, IDelaysDestruction
{
    public enum Phase { Initial, Midpoint, Final }

    const float BeginMidpointRatio = 0.35f;
    const float EndMidpointRatio = 0.65f;

    int probabilityModifier;
    float bonusPerOverkillPercent;
    int sinkDelay, sinkDelayVariance;
    float sinkRate;
    int destructionDelay, destructionDelayVariance;
    float flingForce, flingForceVariance, flingPitch, flingPitchVariance;
    readonly Dictionary<Phase, List<string>> fx = new(), ocl = new(), weapons = new();

    bool activated, midpointExecuted, flungIntoAir, bounced;
    protected int SinkFrame { get; set; }
    protected int MidpointFrame { get; set; }
    protected int DestructionFrame { get; set; }

    public bool IsDelayingDestruction => activated;
    public bool IsActive => activated;
    public bool IsSlowDeathActivated => activated;
    protected float SinkRate => sinkRate;

    /// <summary>The altitude below the ground a sinking object is gone at (read for compatibility; the original never uses it).</summary>
    protected float DestructionAltitude { get; private set; }

    protected override void InitializeDie()
    {
        probabilityModifier = IntSetting("ProbabilityModifier", 10);
        bonusPerOverkillPercent = FloatSetting("ModifierBonusPerOverkillPercent");
        sinkDelay = FramesSetting("SinkDelay");
        sinkDelayVariance = FramesSetting("SinkDelayVariance");
        sinkRate = RuleUnits.Velocity(Setting("SinkRate"));
        destructionDelay = FramesSetting("DestructionDelay");
        destructionDelayVariance = FramesSetting("DestructionDelayVariance");
        DestructionAltitude = FloatSetting("DestructionAltitude", -10);
        flingForce = FloatSetting("FlingForce");
        flingForceVariance = FloatSetting("FlingForceVariance");
        flingPitch = RuleUnits.Angle(Setting("FlingPitch"));
        flingPitchVariance = RuleUnits.Angle(Setting("FlingPitchVariance"));

        ReadPhased("FX", fx);
        ReadPhased("OCL", ocl);
        ReadPhased("Weapon", weapons);
        InitializeSlowDeath();
    }

    protected virtual void InitializeSlowDeath() { }

    /// <summary>Lines such as "FX: INITIAL FX_Something" grouped by phase.</summary>
    void ReadPhased(string key, Dictionary<Phase, List<string>> target)
    {
        foreach (string line in ListSetting(key))
        {
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || !Enum.TryParse<Phase>(words[0], true, out var phase))
                continue;
            if (!target.TryGetValue(phase, out var list))
                target[phase] = list = new();
            list.AddRange(words.Skip(1));
        }
    }

    /// <summary>Selection weight for a given death (more spectacular deaths when badly overkilled).</summary>
    public int ProbabilityFor(DamageInfo killingBlow)
    {
        float overkill = Math.Max(0, killingBlow.Amount - killingBlow.ActualDamageDealt);
        float overkillPercent = Owner.MaxHealth > 0 ? overkill / Owner.MaxHealth : 0;
        return Math.Max(1, probabilityModifier + (int)(overkillPercent * bonusPerOverkillPercent));
    }

    protected override void Die(DamageInfo killingBlow)
    {
        // Only the first slow death to be notified picks one for the whole object (the AI is "dead" after).
        var all = Owner.FindModules<SlowDeathBehavior>().ToList();
        if (all.Any(s => s.activated))
            return;

        var applicable = all.Where(s => s.IsApplicable(killingBlow)).ToList();
        int total = applicable.Sum(s => s.ProbabilityFor(killingBlow));
        int roll = Game.Random.Int(1, Math.Max(1, total));
        foreach (var candidate in applicable)
        {
            roll -= candidate.ProbabilityFor(killingBlow);
            if (roll <= 0)
            {
                candidate.BeginSlowDeath(killingBlow);
                return;
            }
        }
    }

    /// <summary>The original's calcRandomForce: a random direction, pitch and size.</summary>
    Vector3 RandomForce(float minMag, float maxMag, float minPitch, float maxPitch)
    {
        float angle = Game.Random.Float(-MathF.PI, MathF.PI);
        float pitch = Game.Random.Float(minPitch, maxPitch);
        float mag = Game.Random.Float(minMag, maxMag);
        return new Vector3(MathF.Cos(angle) * MathF.Cos(pitch), MathF.Sin(angle) * MathF.Cos(pitch), MathF.Sin(pitch)) * mag;
    }

    public virtual void BeginSlowDeath(DamageInfo killingBlow)
    {
        if (activated)
            return;
        var obj = Owner;
        if (obj.IsKindOf(Kind.Hulk) && Game.HulkMaxLifetimeOverride != -1)
        {
            SinkFrame = 1;
            MidpointFrame = LogicTime.FramesPerSecond / 2 + 1;
            DestructionFrame = LogicTime.FramesPerSecond + 1;
        }
        else
        {
            SinkFrame = sinkDelay + Game.Random.Int(0, sinkDelayVariance);
            DestructionFrame = destructionDelay + Game.Random.Int(0, destructionDelayVariance);
            MidpointFrame = Game.Random.Int((int)(BeginMidpointRatio * DestructionFrame), (int)(EndMidpointRatio * DestructionFrame));
        }

        obj.SetCondition(Condition.Dying);
        obj.SetStatus(Core.Status.Unselectable);
        obj.AI?.Stop(Commands.CommandSource.AI);
        Game.Events.Deselect(obj);

        int now = Frame;
        if (flingForce > 0)
        {
            if (obj.IsDisabledBy("DISABLED_HELD"))
                obj.FindModule<Behaviors.ISlavedUpdate>()?.OnSlaverDie(killingBlow);
            if (obj.FindModule<PhysicsBehavior>() is { } physics)
            {
                const float MinAltitude = 1;
                if (obj.HeightAboveTerrain < MinAltitude)
                    obj.SetPosition(obj.Position with { Z = obj.Position.Z + MinAltitude });
                var force = RandomForce(flingForce, flingForce + flingForceVariance, flingPitch, flingPitch + flingPitchVariance);
                physics.AllowToFall = true;
                physics.ApplyForce(force);
                physics.ExtraBounciness = -1;
                physics.ExtraFriction = -3 * LogicTime.SecondsPerFrame;
                physics.AllowBouncing = true;
                obj.SetOrientation(MathF.Atan2(force.Y, force.X));
                obj.Pitch = 0;
                obj.Roll = 0;
                obj.SetCondition("EXPLODED_FLAILING");
                flungIntoAir = true;
            }
            obj.WakeUpdate(this);
        }
        else
            obj.SleepUpdate(this, Math.Max(0, Math.Min(SinkFrame, Math.Min(DestructionFrame, MidpointFrame))));

        SinkFrame += now;
        DestructionFrame += now;
        MidpointFrame += now;
        activated = true;
        DoPhaseStuff(Phase.Initial);
    }

    public FlagSet DisabledTypesToProcess => DisabledMask.All;

    public virtual int Update()
    {
        if (!activated)
            return LogicTime.SleepForever;

        var obj = Owner;
        int now = Frame;
        if (flungIntoAir && !bounced)
        {
            // In the air the clock stops.
            SinkFrame++;
            MidpointFrame++;
            DestructionFrame++;
            if (!obj.IsAboveTerrain)
            {
                obj.SetCondition("EXPLODED_FLAILING", false);
                obj.SetCondition("EXPLODED_BOUNCING");
                bounced = true;
            }
            // Caught in a tree: hang there as if parachuting.
            if (obj.FindModule<PhysicsBehavior>() is { } phys && Game.FindObject(phys.LastCollideeId) is { } tree && tree.IsKindOf(Kind.Shrubbery))
            {
                obj.SetDisabled("DISABLED_HELD");
                obj.SetCondition("EXPLODED_FLAILING", false);
                obj.SetCondition("EXPLODED_BOUNCING", false);
                obj.SetCondition("PARACHUTING");
                obj.SetPosition(obj.Position with { Z = obj.Position.Z - sinkRate * 50 });
                if (!obj.IsAboveTerrain)
                {
                    Game.DestroyObject(obj);
                    return LogicTime.SleepForever;
                }
            }
        }

        if (now >= SinkFrame && sinkRate > 0)
        {
            obj.SetDisabled("DISABLED_HELD");
            obj.SetPosition(obj.Position with { Z = obj.Position.Z - sinkRate });
        }

        if (now >= MidpointFrame && !midpointExecuted)
        {
            DoPhaseStuff(Phase.Midpoint);
            midpointExecuted = true;
        }

        if (now >= DestructionFrame)
        {
            DoPhaseStuff(Phase.Final);
            Game.DestroyObject(obj);
        }
        return 0;
    }

    protected void DoPhaseStuff(Phase phase)
    {
        if (fx.TryGetValue(phase, out var fxList))
            Game.Events.PlayFx(fxList[Game.Random.Int(0, fxList.Count - 1)], Owner.Position, Owner);
        if (ocl.TryGetValue(phase, out var oclList))
            Game.ObjectCreation.Run(oclList[Game.Random.Int(0, oclList.Count - 1)], Owner, Owner.Position);
        if (weapons.TryGetValue(phase, out var weaponList))
            Game.Damage.FireTemporaryWeapon(weaponList[Game.Random.Int(0, weaponList.Count - 1)], Owner, Owner.Position);
    }
}
