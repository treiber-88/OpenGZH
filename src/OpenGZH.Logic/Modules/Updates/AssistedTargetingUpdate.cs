using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// Lets a Patriot battery help a neighbour (the original's AssistedTargetingUpdate): when a nearby
/// battery fires a RequestAssistRange weapon, a free one switches to AssistingWeaponSlot for
/// AssistingClipSize shots at the same target, with feedback lasers (LaserFromAssisted, LaserToTarget).
/// </summary>
public sealed class AssistedTargetingUpdate : Module, IUpdateModule, ICreateModule
{
    uint assistVictim;

    WeaponSlot AssistSlot => Setting("AssistingWeaponSlot").ToUpperInvariant() switch
    {
        "SECONDARY" or "SECONDARY_WEAPON" => WeaponSlot.Secondary,
        "TERTIARY" or "TERTIARY_WEAPON" => WeaponSlot.Tertiary,
        _ => WeaponSlot.Primary,
    };

    public void OnCreate() => Owner.SleepUpdate(this);

    /// <summary>Able to attack and with the current weapon ready to fire (the original's isFreeToAssist).</summary>
    public bool IsFreeToAssist
    {
        get
        {
            var me = Owner;
            if (me.IsEffectivelyDead || me.IsUnderConstruction || me.IsDisabled || !me.Weapons.HasAnyWeapon)
                return false;
            return me.Weapons.Current is { IsReady: true };
        }
    }

    public void AssistAttack(GameObject requestingObject, GameObject victim)
    {
        var me = Owner;
        if (me.AI is not { } ai)
            return;
        // A temporary lock: released again once this attack is over.
        me.Weapons.LockSlot(AssistSlot);
        ai.AttackObject(victim, CommandSource.AI, IntSetting("AssistingClipSize"));
        assistVictim = victim.Id;
        me.WakeUpdate(this);
        // (The original looks both lasers up by LaserFromAssisted's name.)
        string laser = Setting("LaserFromAssisted");
        MakeFeedbackLaser(laser, requestingObject, me);
        MakeFeedbackLaser(laser, me, victim);
    }

    void MakeFeedbackLaser(string laserTemplate, GameObject from, GameObject to)
    {
        if (laserTemplate.Length == 0 || Game.Templates.Find(laserTemplate) is not { } template)
            return;
        var laser = Game.CreateObject(template, Owner.Owner, from.Position, 0, Owner.Owner.DefaultTeam);
        if (laser.FindModule<SpecialPowers.LaserUpdate>() is not { } beam)
        {
            Game.DestroyObject(laser);
            return;
        }
        beam.Init(from, to, "");
    }

    public int Update()
    {
        if (assistVictim == 0)
            return Core.LogicTime.SleepForever;
        var ai = Owner.AI;
        if (ai == null || ai.TargetId != assistVictim || ai.State != AI.AIState.AttackingObject)
        {
            Owner.Weapons.ReleaseLock();
            assistVictim = 0;
            return Core.LogicTime.SleepForever;
        }
        return 0;
    }
}
