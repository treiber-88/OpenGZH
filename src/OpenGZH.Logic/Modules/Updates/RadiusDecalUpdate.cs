using System.Numerics;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// A target circle on the ground that an object keeps up (the original's RadiusDecalUpdate): an
/// air strike's aim point, shown until the attack is over when KillWhenNoLongerAttacking. It lives in
/// the effect registry (<see cref="Effects.EffectRegistry.RadiusDecals"/>) for the client to draw.
/// </summary>
public sealed class RadiusDecalUpdate : Module, IUpdateModule, ICreateModule, IDieModule
{
    int decalId;
    public bool IsActive => decalId != 0;
    public bool KillWhenNoLongerAttacking { get; set; }

    public void OnCreate() => Owner.SleepUpdate(this);

    public void CreateRadiusDecal(Effects.RadiusDecalTemplate? template, float radius, Vector3 position)
    {
        KillRadiusDecal();
        decalId = Game.Effects.CreateRadiusDecal(template, position, radius, Owner.Owner, Owner);
        if (IsActive)
            Owner.WakeUpdate(this);
    }

    public void KillRadiusDecal()
    {
        if (decalId != 0)
            Game.Effects.DestroyRadiusDecal(decalId);
        decalId = 0;
        Owner.SleepUpdate(this);
    }

    public void OnDie(Combat.DamageInfo killingBlow) => KillRadiusDecal();

    public int Update()
    {
        if (KillWhenNoLongerAttacking && !Owner.HasStatus(Status.IsAttacking))
        {
            KillRadiusDecal();
            return LogicTime.SleepForever;
        }
        return 0;
    }
}
