using System.Numerics;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Modules.Damage;

/// <summary>
/// Effects at bones, per damage state (the original's BoneFXUpdate): up to 8 FX lists, object
/// creation lists and particle systems for each state, "bone:Name OnlyOnce:Yes/No minDelay maxDelay
/// FXList:/OCL:/PSys:Name", each repeating after a random delay unless OnlyOnce. The state comes from
/// the object's <see cref="BoneFXDamage"/>; changing state stops the old state's particle systems.
/// </summary>
public sealed class BoneFXUpdate : Module, IUpdateModule, IOwnershipModule
{
    const int MaxBones = 8;
    static readonly string[] stateNames = ["Pristine", "Damaged", "ReallyDamaged", "Rubble"];

    readonly record struct Entry(string Bone, bool OnlyOnce, float MinDelay, float MaxDelay, string Name);

    readonly Entry?[,] fxLists = new Entry?[4, MaxBones], ocls = new Entry?[4, MaxBones], particleSystems = new Entry?[4, MaxBones];
    readonly int[,] nextFxFrame = new int[4, MaxBones], nextOclFrame = new int[4, MaxBones], nextParticleFrame = new int[4, MaxBones];
    readonly Vector3[,] fxBonePositions = new Vector3[4, MaxBones], oclBonePositions = new Vector3[4, MaxBones], psBonePositions = new Vector3[4, MaxBones];
    readonly bool[] bonesResolved = new bool[4];
    readonly List<uint> particleSystemIds = new();
    bool active;
    int curBodyState;
    FlagSet damageFxTypes, damageOclTypes, damageParticleTypes;

    // The particle systems' delays come from the client's random numbers in the original.
    static readonly Random ClientRandom = new();

    protected override void Initialize()
    {
        damageFxTypes = Flags.DamageType.Parse(Setting("DamageFXTypes", "ALL"));
        damageOclTypes = Flags.DamageType.Parse(Setting("DamageOCLTypes", "ALL"));
        damageParticleTypes = Flags.DamageType.Parse(Setting("DamageParticleTypes", "ALL"));
        for (int state = 0; state < 4; state++)
        {
            for (int i = 0; i < MaxBones; i++)
            {
                fxLists[state, i] = Parse(Setting($"{stateNames[state]}FXList{i + 1}"), "FXLIST");
                ocls[state, i] = Parse(Setting($"{stateNames[state]}OCL{i + 1}"), "OCL");
                particleSystems[state, i] = Parse(Setting($"{stateNames[state]}ParticleSystem{i + 1}"), "PSYS");
                nextFxFrame[state, i] = nextOclFrame[state, i] = nextParticleFrame[state, i] = -1;
            }
        }
    }

    static Entry? Parse(string text, string nameKey)
    {
        if (text.Length == 0)
            return null;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string bone = "", name = "";
        bool onlyOnce = true;
        var delays = new List<float>();
        foreach (string word in words)
        {
            int colon = word.IndexOf(':');
            if (colon < 0)
            {
                delays.Add(OpenGZH.Core.Rules.RuleValue.Float(word) / LogicTime.MillisecondsPerFrame);
                continue;
            }
            string key = word[..colon].ToUpperInvariant(), value = word[(colon + 1)..];
            if (key == "BONE")
                bone = value;
            else if (key == "ONLYONCE")
                onlyOnce = OpenGZH.Core.Rules.RuleValue.Bool(value);
            else if (key == nameKey)
                name = value;
        }
        if (name.Length == 0)
            return null;
        return new Entry(bone, onlyOnce, delays.Count > 0 ? delays[0] : 0, delays.Count > 1 ? delays[1] : delays.Count > 0 ? delays[0] : 0, name);
    }

    float LogicDelay(Entry e) => Game.Random.Float(e.MinDelay, e.MaxDelay);
    static float ClientDelay(Entry e) => e.MinDelay + (float)ClientRandom.NextDouble() * (e.MaxDelay - e.MinDelay);

    public int Update()
    {
        int now = Frame;
        if (!active)
        {
            InitTimes();
            active = true;
        }
        int s = curBodyState;
        for (int i = 0; i < MaxBones; i++)
        {
            if (nextFxFrame[s, i] != -1 && nextFxFrame[s, i] <= now && fxLists[s, i] is { } fx)
            {
                DoFxListAtBone(fx.Name, i);
                nextFxFrame[s, i] = fx.OnlyOnce ? -1 : Frame + (int)LogicDelay(fx);
            }
            if (nextOclFrame[s, i] != -1 && nextOclFrame[s, i] <= now && ocls[s, i] is { } ocl)
            {
                DoOclAtBone(ocl.Name, i);
                nextOclFrame[s, i] = ocl.OnlyOnce ? -1 : Frame + (int)LogicDelay(ocl);
            }
            if (nextParticleFrame[s, i] != -1 && nextParticleFrame[s, i] <= now && particleSystems[s, i] is { } ps)
            {
                DoParticleSystemAtBone(ps.Name, i);
                nextParticleFrame[s, i] = ps.OnlyOnce ? -1 : Frame + (int)ClientDelay(ps);
            }
        }
        return 0;
    }

    void InitTimes()
    {
        int now = Frame, s = curBodyState;
        for (int i = 0; i < MaxBones; i++)
        {
            nextFxFrame[s, i] = fxLists[s, i] is { Bone.Length: > 0 } fx ? now + (int)LogicDelay(fx) : -1;
            nextOclFrame[s, i] = ocls[s, i] is { Bone.Length: > 0 } ocl ? now + (int)LogicDelay(ocl) : -1;
            nextParticleFrame[s, i] = particleSystems[s, i] is { Bone.Length: > 0 } ps ? now + (int)ClientDelay(ps) : -1;
        }
    }

    public void ChangeBodyDamageState(BodyDamageState oldState, BodyDamageState newState)
    {
        curBodyState = (int)newState;
        KillRunningParticleSystems();
        InitTimes();
    }

    bool Allowed(FlagSet types) =>
        Owner.Body?.LastDamage is not { DamageType.Length: > 0 } last || types.Has(Flags.DamageType.IndexOf(last.DamageType));

    void DoFxListAtBone(string fx, int i)
    {
        if (!bonesResolved[curBodyState])
            ResolveBoneLocations();
        if (Allowed(damageFxTypes))
            Game.Events.PlayFx(fx, Art.ModelBones.ToWorld(Owner, fxBonePositions[curBodyState, i]));
    }

    void DoOclAtBone(string ocl, int i)
    {
        if (!bonesResolved[curBodyState])
            ResolveBoneLocations();
        if (Allowed(damageOclTypes))
            Game.ObjectCreation.Run(ocl, Owner, Art.ModelBones.ToWorld(Owner, oclBonePositions[curBodyState, i]));
    }

    void DoParticleSystemAtBone(string template, int i)
    {
        if (!bonesResolved[curBodyState])
            ResolveBoneLocations();
        if (!Allowed(damageParticleTypes))
            return;
        uint id = Game.Events.AttachParticleSystem(template, psBonePositions[curBodyState, i], Owner);
        if (id != 0)
            particleSystemIds.Add(id);
    }

    void KillRunningParticleSystems()
    {
        foreach (uint id in particleSystemIds)
            Game.Events.StopParticleSystem(id);
        particleSystemIds.Clear();
    }

    void ResolveBoneLocations()
    {
        int s = curBodyState;
        Vector3 Bone(string name) => Game.Bones.TryGetLocalBone(Owner, name, out var pos) ? pos : Vector3.Zero;
        for (int i = 0; i < MaxBones; i++)
        {
            if (fxLists[s, i] is { Bone.Length: > 0 } fx)
                fxBonePositions[s, i] = Bone(fx.Bone);
            if (ocls[s, i] is { Bone.Length: > 0 } ocl)
                oclBonePositions[s, i] = Bone(ocl.Bone);
            if (particleSystems[s, i] is { Bone.Length: > 0 } ps)
                psBonePositions[s, i] = Bone(ps.Bone);
        }
        bonesResolved[s] = true;
    }

    /// <summary>A building that has finished falling stops all its bone effects.</summary>
    public void StopAllBoneFX()
    {
        for (int s = 0; s < 4; s++)
            for (int i = 0; i < MaxBones; i++)
                nextFxFrame[s, i] = nextOclFrame[s, i] = nextParticleFrame[s, i] = -1;
        KillRunningParticleSystems();
    }

    public void OnDelete() => KillRunningParticleSystems();
}

/// <summary>Tells the object's <see cref="BoneFXUpdate"/> when the damage state changes (the original's BoneFXDamage).</summary>
public sealed class BoneFXDamage : Module, IDamageModule
{
    public void OnBodyDamageStateChange(BodyDamageState oldState, BodyDamageState newState) =>
        Owner.FindModule<BoneFXUpdate>()?.ChangeBodyDamageState(oldState, newState);
}
