using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.SlowDeath;

/// <summary>
/// A death that takes time: the object plays its dying animation, may be flung into the air, sinks
/// into the ground, and is removed after DestructionDelay. Effects run at three moments:
/// INITIAL (at death), MIDPOINT (halfway) and FINAL (when removed).
///
/// An object may have several slow deaths; when it dies, one applicable to the killing blow is picked
/// at random, weighted by ProbabilityModifier (heavy overkill favours ModifierBonusPerOverkillPercent).
/// </summary>
public class SlowDeathBehavior : DieModule, IUpdateModule, IDelaysDestruction
{
    public enum Phase { Initial, Midpoint, Final }

    int probabilityModifier;
    float bonusPerOverkillPercent;
    int sinkDelay, sinkDelayVariance;
    float sinkRate;
    int destructionDelay, destructionDelayVariance;
    float destructionAltitude;
    float flingForce, flingForceVariance, flingPitch, flingPitchVariance;
    readonly Dictionary<Phase, List<string>> fx = new(), ocl = new(), weapons = new();

    bool active;
    int sinkFrame, midpointFrame, destructionFrame;
    bool midpointDone;
    bool flung;

    public bool IsDelayingDestruction => active;
    public bool IsActive => active;

    protected override void InitializeDie()
    {
        probabilityModifier = IntSetting("ProbabilityModifier", 10);
        bonusPerOverkillPercent = FloatSetting("ModifierBonusPerOverkillPercent");
        sinkDelay = FramesSetting("SinkDelay");
        sinkDelayVariance = FramesSetting("SinkDelayVariance");
        sinkRate = RuleUnits.Velocity(Setting("SinkRate"));
        destructionDelay = FramesSetting("DestructionDelay");
        destructionDelayVariance = FramesSetting("DestructionDelayVariance");
        destructionAltitude = FloatSetting("DestructionAltitude", -10);
        flingForce = FloatSetting("FlingForce");
        flingForceVariance = FloatSetting("FlingForceVariance");
        flingPitch = RuleUnits.Angle(Setting("FlingPitch"));
        flingPitchVariance = RuleUnits.Angle(Setting("FlingPitchVariance"));

        ReadPhased("FX", fx);
        ReadPhased("OCL", ocl);
        ReadPhased("Weapon", weapons);
    }

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
        // Only the first slow death to be notified picks one for the whole object.
        var all = Owner.FindModules<SlowDeathBehavior>().ToList();
        if (all.Any(s => s.active))
            return;

        var applicable = all.Where(s => s.IsApplicable(killingBlow)).ToList();
        int total = applicable.Sum(s => s.ProbabilityFor(killingBlow));
        int roll = Game.Random.Int(1, Math.Max(1, total));
        foreach (var candidate in applicable)
        {
            roll -= candidate.ProbabilityFor(killingBlow);
            if (roll <= 0)
            {
                candidate.Begin(killingBlow);
                return;
            }
        }
    }

    protected virtual void Begin(DamageInfo killingBlow)
    {
        active = true;
        int now = Frame;
        sinkFrame = now + sinkDelay + Game.Random.Int(0, sinkDelayVariance);
        destructionFrame = now + destructionDelay + Game.Random.Int(0, destructionDelayVariance);
        midpointFrame = now + (destructionFrame - now) / 2;

        Owner.SetCondition(Condition.Dying);
        Owner.SetStatus(Core.Status.Unselectable);
        Owner.AI?.Stop(Commands.CommandSource.AI);

        if (flingForce > 0 && Owner.FindModule<PhysicsBehavior>() is { } physics)
        {
            float force = flingForce + Game.Random.Float(-flingForceVariance, flingForceVariance);
            float pitch = flingPitch + Game.Random.Float(-flingPitchVariance, flingPitchVariance);
            float yaw = Game.Random.Float(0, MathF.Tau);
            physics.ApplyForce(new Vector3(MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch)) * force);
            Owner.SetCondition("EXPLODED_FLAILING");
            flung = true;
        }

        RunPhase(Phase.Initial);
        Owner.WakeUpdate(this);
    }

    public FlagSet DisabledTypesToProcess => DisabledMask.All;

    public int Update()
    {
        if (!active)
            return 1000;

        int now = Frame;
        if (flung && Owner.Position.Z <= Game.Terrain.LayerHeight(Owner.Position.X, Owner.Position.Y) + 0.5f)
        {
            Owner.SetCondition("EXPLODED_FLAILING", false);
            Owner.SetCondition("EXPLODED_BOUNCING");
            flung = false;
        }

        if (now >= sinkFrame && sinkRate > 0)
            Owner.SetPosition(Owner.Position with { Z = Owner.Position.Z - sinkRate });

        if (!midpointDone && now >= midpointFrame)
        {
            midpointDone = true;
            RunPhase(Phase.Midpoint);
        }

        bool sunk = sinkRate > 0 && Owner.Position.Z < Game.Terrain.GroundHeight(Owner.Position) + destructionAltitude;
        if (now >= destructionFrame || sunk)
        {
            RunPhase(Phase.Final);
            active = false;
            Game.DestroyObject(Owner);
            return 1000;
        }
        return 0;
    }

    protected void RunPhase(Phase phase)
    {
        if (fx.TryGetValue(phase, out var fxList))
            Game.Events.PlayFx(fxList[Game.Random.Int(0, fxList.Count - 1)], Owner.Position, Owner);
        if (ocl.TryGetValue(phase, out var oclList))
            Game.ObjectCreation.Run(oclList[Game.Random.Int(0, oclList.Count - 1)], Owner, Owner.Position);
        if (weapons.TryGetValue(phase, out var weaponList))
            Game.Damage.FireTemporaryWeapon(weaponList[Game.Random.Int(0, weaponList.Count - 1)], Owner, Owner.Position);
    }
}
