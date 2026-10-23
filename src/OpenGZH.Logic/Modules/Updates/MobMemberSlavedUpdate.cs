using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Behaviors;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// A GLA Angry Mob member (the original's MobMemberSlavedUpdate): runs to keep up with the mob's
/// invisible nexus beyond MustCatchUpRadius (dying after CatchUpCrisisBailTime too far away),
/// mills about with random wander/panic gaits, attacks the nexus's victim, and with
/// Squirrelliness some members pick their own targets.
/// </summary>
public sealed class MobMemberSlavedUpdate : Module, IUpdateModule, ICreateModule, ISlavedUpdate
{
    uint slaver;
    int framesToWait;
    uint primaryVictimId;
    float squirrelliness;
    int catchUpCrisisTimer;

    public bool IsSelfTasking { get; private set; }
    public uint SlaverId => slaver;

    /// <summary>Each member's own tint (the original's personal color), drawn by the client.</summary>
    public (float R, float G, float B) PersonalColor { get; private set; }

    public void OnCreate()
    {
        framesToWait = Game.Random.Int(0, 20);
        PersonalColor = (Game.Random.Float(0.2f, 0.4f), Game.Random.Float(0.2f, 0.4f), Game.Random.Float(0.2f, 0.4f));
        squirrelliness = Math.Min(1f, Math.Max(0, FloatSetting("Squirrelliness")));
    }

    public void OnEnslave(GameObject slaverObject) => slaver = slaverObject.Id;

    public void OnSlaverDie(DamageInfo damage) => StopSlavedEffects();

    // (The original makes members go prone when the nexus is hit; ZH infantry have no prone state.)
    public void OnSlaverDamage(DamageInfo damage) { }

    void StopSlavedEffects()
    {
        slaver = 0;
        Owner.SetStatus(Status.Unselectable, false);
        Owner.SetDisabled("DISABLED_HELD", false);
    }

    static readonly string[] clearedWhenUpgraded = ["RELOADING_A", "BETWEEN_FIRING_SHOTS_A", "PREATTACK_A", "FIRING_A", "USING_WEAPON_A"];

    public int Update()
    {
        var me = Owner;
        var master = Game.FindObject(slaver);
        if (master == null)
        {
            StopSlavedEffects();
            me.Kill();
            return 0;
        }
        if (me.AI is not { } myAi || master.AI is not { } masterAi)
            return 0;

        // Upgraded mobs (AK-47s) hide the weapon-A animation states.
        if (me.ModelConditions.Has(Flags.ModelCondition.IndexOf("WEAPONSET_PLAYER_UPGRADE")))
            foreach (var condition in clearedWhenUpgraded)
                me.SetCondition(condition, false);

        if (++framesToWait < 16)
            return 0;
        framesToWait = 0;
        if (myAi.CurrentLocomotor == null)
            return 0;

        var victim = Game.FindObject(myAi.TargetId);
        if (Game.FindObject(masterAi.TargetId) is { } masterVictim)
            primaryVictimId = masterVictim.Id;
        var primaryVictim = Game.FindObject(primaryVictimId);

        float masterPathDistToGoal = masterAi.LocomotorDistanceToGoal();
        float myPathDistToGoal = myAi.LocomotorDistanceToGoal();
        float catchUpSq = System.Numerics.Vector3.DistanceSquared(me.Position, master.Position);
        float mustCatchUp = IntSetting("MustCatchUpRadius", 50);
        if (catchUpSq > mustCatchUp * mustCatchUp)
        {
            if (masterAi.IsMoving)
            {
                myAi.SetLocomotorSet(masterPathDistToGoal > myPathDistToGoal ? "SET_WANDER" : "SET_PANIC");
                var goal = masterAi.Destination;
                if (goal.Length() < 1)
                    myAi.MoveTo(master.Position, CommandSource.AI);
                else if ((myAi.Destination - goal).Length() > 5 * Formats.Maps.MapFile.CellSize)
                    myAi.MoveTo(goal, CommandSource.AI);
            }
            else
            {
                myAi.SetLocomotorSet("SET_PANIC");
                myAi.MoveTo(master.Position, CommandSource.AI);
            }
            if (catchUpSq > 9 * mustCatchUp * mustCatchUp)
            {
                int bail = IntSetting("CatchUpCrisisBailTime", 999999);
                if (++catchUpCrisisTimer > bail)
                {
                    me.Kill();
                    return 0;
                }
                if (catchUpCrisisTimer > bail / 3)
                    myAi.MoveTo(master.Position, CommandSource.AI);
            }
        }
        else if (myAi.IsMoving)
        {
            catchUpCrisisTimer = 0;
            switch (Game.Random.Int(0, 10))
            {
                case 1: myAi.SetLocomotorSet("SET_WANDER"); break;
                case 2: myAi.SetLocomotorSet("SET_PANIC"); break;
                case 3: myAi.SetLocomotorSet("SET_NORMAL"); break;
            }
        }
        else
        {
            catchUpCrisisTimer = 0;
            if (master.FindModule<SpawnBehavior>() is not { } spawner)
                return 0;
            if (masterAi.IsIdle)
            {
                myAi.Stop(CommandSource.AI);
                primaryVictimId = 0;
                return 0;
            }
            if (spawner.MaySpawnSelfTaskAI(squirrelliness) && myAi.LastCommandSource != CommandSource.AI)
            {
                if (myAi.GetNextMoodTarget() is { } newTarget && newTarget != victim)
                {
                    victim = newTarget;
                    myAi.AttackObject(newTarget, CommandSource.AI, 999);
                    IsSelfTasking = true;
                }
            }
            if (victim == null)
            {
                if (primaryVictim != null)
                    myAi.AttackObject(primaryVictim, CommandSource.AI, 999);
                IsSelfTasking = false;
            }
        }
        return 0;
    }
}
