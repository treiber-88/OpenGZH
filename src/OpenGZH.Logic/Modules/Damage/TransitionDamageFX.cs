using System.Numerics;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Modules.Damage;

/// <summary>
/// Effects when the body gets worse (the original's TransitionDamageFX): on reaching Damaged,
/// ReallyDamaged or Rubble it plays that state's FX lists, runs its object creation lists and starts
/// its particle systems (up to 12 of each, "Loc: X:.. Y:.. Z:.." or "Bone:name RandomBone:Yes/No"),
/// filtered by the last damage's type (DamageFXTypes / DamageOCLTypes / DamageParticleTypes).
/// Particle systems of the state being left are stopped.
/// </summary>
public sealed class TransitionDamageFX : Module, IDamageModule
{
    const int MaxFx = 12;
    static readonly string[] stateNames = ["Pristine", "Damaged", "ReallyDamaged", "Rubble"];

    readonly record struct LocInfo(Vector3 Loc, string Bone, bool RandomBone);
    readonly record struct Entry(LocInfo Loc, string Name);

    readonly Entry?[,] fxLists = new Entry?[4, MaxFx], ocls = new Entry?[4, MaxFx], particleSystems = new Entry?[4, MaxFx];
    readonly uint[,] particleSystemIds = new uint[4, MaxFx];
    FlagSet damageFxTypes, damageOclTypes, damageParticleTypes;

    protected override void Initialize()
    {
        damageFxTypes = Flags.DamageType.Parse(Setting("DamageFXTypes", "ALL"));
        damageOclTypes = Flags.DamageType.Parse(Setting("DamageOCLTypes", "ALL"));
        damageParticleTypes = Flags.DamageType.Parse(Setting("DamageParticleTypes", "ALL"));
        for (int state = 1; state < 4; state++)
        {
            for (int i = 0; i < MaxFx; i++)
            {
                fxLists[state, i] = Parse(Setting($"{stateNames[state]}FXList{i + 1}"), "FXLIST");
                ocls[state, i] = Parse(Setting($"{stateNames[state]}OCL{i + 1}"), "OCL");
                particleSystems[state, i] = Parse(Setting($"{stateNames[state]}ParticleSystem{i + 1}"), "PSYS");
            }
        }
    }

    /// <summary>"Loc: X:0 Y:0 Z:0 FXList:FX_Name" or "Bone:Smoke RandomBone:Yes PSys:Name".</summary>
    static Entry? Parse(string text, string nameKey)
    {
        if (text.Length == 0)
            return null;
        var tokens = text.Replace(':', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var loc = Vector3.Zero;
        string bone = "", name = "";
        bool randomBone = false;
        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            string key = tokens[i].ToUpperInvariant();
            string value = tokens[i + 1];
            switch (key)
            {
                case "X": loc.X = OpenGZH.Core.Rules.RuleValue.Float(value); i++; break;
                case "Y": loc.Y = OpenGZH.Core.Rules.RuleValue.Float(value); i++; break;
                case "Z": loc.Z = OpenGZH.Core.Rules.RuleValue.Float(value); i++; break;
                case "BONE": bone = value; i++; break;
                case "RANDOMBONE": randomBone = OpenGZH.Core.Rules.RuleValue.Bool(value); i++; break;
                default:
                    if (key == nameKey)
                    {
                        name = value;
                        i++;
                    }
                    break;
            }
        }
        return name.Length == 0 ? null : new Entry(new LocInfo(loc, bone, randomBone), name);
    }

    /// <summary>The effect's object-relative position (the original's getLocalEffectPos).</summary>
    Vector3 LocalEffectPos(LocInfo info)
    {
        if (info.Bone.Length == 0)
            return info.Loc;
        if (!info.RandomBone)
            return Game.Bones.TryGetLocalBone(Owner, info.Bone, out var pos) ? pos : info.Loc;
        var positions = new List<Vector3>();
        for (int i = 1; i <= 32; i++)
        {
            if (!Game.Bones.TryGetLocalBone(Owner, $"{info.Bone}{i:00}", out var pos))
                break;
            positions.Add(pos);
        }
        if (positions.Count == 0)
            return info.Loc;
        return positions[Game.Random.Int(0, positions.Count - 1)];
    }

    bool TypeAllowed(FlagSet types)
    {
        // No damage yet: everything plays.
        if (Owner.Body?.LastDamage is not { } last || last.DamageType.Length == 0)
            return true;
        return types.Has(Flags.DamageType.IndexOf(last.DamageType));
    }

    public void OnBodyDamageStateChange(BodyDamageState oldState, BodyDamageState newState)
    {
        int oldIndex = (int)oldState, newIndex = (int)newState;
        for (int i = 0; i < MaxFx; i++)
        {
            if (particleSystemIds[oldIndex, i] != 0)
            {
                Game.Events.StopParticleSystem(particleSystemIds[oldIndex, i]);
                particleSystemIds[oldIndex, i] = 0;
            }
        }
        if (newIndex <= oldIndex)
            return;
        var damageSource = Owner.Body?.LastDamage is { } last ? Game.FindObject(last.SourceId) : null;
        for (int i = 0; i < MaxFx; i++)
        {
            if (fxLists[newIndex, i] is { } fx && TypeAllowed(damageFxTypes))
                Game.Events.PlayFx(fx.Name, Art.ModelBones.ToWorld(Owner, LocalEffectPos(fx.Loc)));
            if (ocls[newIndex, i] is { } ocl && TypeAllowed(damageOclTypes))
            {
                var pos = Art.ModelBones.ToWorld(Owner, LocalEffectPos(ocl.Loc));
                Game.ObjectCreation.Run(ocl.Name, Owner, pos, secondary: damageSource?.Position);
            }
            if (particleSystems[newIndex, i] is { } ps && TypeAllowed(damageParticleTypes))
                particleSystemIds[newIndex, i] = Game.Events.AttachParticleSystem(ps.Name, LocalEffectPos(ps.Loc), Owner);
        }
    }
}
