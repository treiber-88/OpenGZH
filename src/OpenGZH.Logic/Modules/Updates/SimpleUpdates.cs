using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>Destroys (or kills) the object after a random time between MinLifetime and MaxLifetime.</summary>
public sealed class LifetimeUpdate : Module, IUpdateModule, ICreateModule
{
    int dieFrame;

    /// <summary>The frame the object will die on.</summary>
    public int DieFrame => dieFrame;

    public void OnCreate()
    {
        int min = FramesSetting("MinLifetime");
        int max = Math.Max(min, FramesSetting("MaxLifetime", FloatSetting("MinLifetime")));
        dieFrame = Frame + Game.Random.Int(min, max);
    }

    public void SetLifetime(int frames)
    {
        dieFrame = Frame + frames;
        Owner.WakeUpdate(this);
    }

    public int Update()
    {
        if (Frame < dieFrame)
            return Math.Max(0, dieFrame - Frame - 1);
        Owner.Kill();
        return 1000;
    }
}

/// <summary>Removes the object (no death effects) after a random time.</summary>
public sealed class DeletionUpdate : Module, IUpdateModule, ICreateModule
{
    int deleteFrame;

    public void OnCreate()
    {
        int min = FramesSetting("MinLifetime");
        int max = Math.Max(min, FramesSetting("MaxLifetime", FloatSetting("MinLifetime")));
        deleteFrame = Frame + Game.Random.Int(min, max);
    }

    public void SetLifetime(int frames)
    {
        deleteFrame = Frame + frames;
        Owner.WakeUpdate(this);
    }

    public int Update()
    {
        if (Frame < deleteFrame)
            return Math.Max(0, deleteFrame - Frame - 1);
        Game.DestroyObject(Owner);
        return 1000;
    }
}

/// <summary>Kills the object when it falls below (or rises above) a height, e.g. aircraft that crash.</summary>
public sealed class HeightDieUpdate : Module, IUpdateModule
{
    float targetHeight;
    bool onlyWhenMovingDown;
    int initialDelay;
    float lastZ = float.MaxValue;

    protected override void Initialize()
    {
        targetHeight = FloatSetting("TargetHeight");
        onlyWhenMovingDown = BoolSetting("OnlyWhenMovingDown");
        initialDelay = FramesSetting("InitialDelay");
    }

    public int Update()
    {
        if (Frame - Owner.CreatedFrame < initialDelay)
            return 0;
        // Height is measured above the ground (or water surface) below the object.
        float ground = Game.Terrain.LayerHeight(Owner.Position.X, Owner.Position.Y);
        bool movingDown = Owner.Position.Z < lastZ;
        lastZ = Owner.Position.Z;
        if (Owner.Position.Z - ground <= targetHeight && (!onlyWhenMovingDown || movingDown))
            Owner.Kill();
        return 0;
    }
}

/// <summary>
/// A unit spawned and controlled by another (the original's SlavedUpdate: drones, Stinger Site
/// soldiers). Four times a second it may repair its master, go to the master's target, scout ahead
/// of a moving master, or come back to its guard point. With no master left it is disabled as
/// unmanned. Ranges of 0 (the default) turn a behaviour off: Stinger soldiers, with no settings,
/// are never moved by this module.
/// </summary>
public sealed class SlavedUpdate : Module, IUpdateModule, ICreateModule, Behaviors.ISlavedUpdate
{
    const int SlavedUpdateRate = LogicTime.FramesPerSecond / 4;
    const float StrayMultiplier = 2.0f;
    const float CloseEnoughSqr = 15 * 15;

    enum RepairState { None, Unpacking, Packing, Ready, Welding, Extending, Retracting }

    uint slaver;
    Vector3 guardPointOffset;
    int framesToWait;
    RepairState repairState;
    bool repairing;

    int guardMaxRange, guardWanderRange, attackRange, attackWanderRange, scoutRange, scoutWanderRange;
    int distToTargetToGrantRangeBonus, repairRange, repairWhenHealthBelowPercentage;
    int minReadyFrames, maxReadyFrames, minWeldFrames, maxWeldFrames;
    float repairMinAltitude, repairMaxAltitude, repairRatePerSecond;
    string weldingSysName = "", weldingFxBone = "";
    bool stayOnSameLayerAsMaster;

    // Welding sparks last as long as the weld (the original sets the particle system's lifetime).
    readonly List<(int Id, int EndFrame)> weldingSystems = new();

    protected override void Initialize()
    {
        guardMaxRange = IntSetting("GuardMaxRange");
        guardWanderRange = IntSetting("GuardWanderRange");
        attackRange = IntSetting("AttackRange");
        attackWanderRange = IntSetting("AttackWanderRange");
        scoutRange = IntSetting("ScoutRange");
        scoutWanderRange = IntSetting("ScoutWanderRange");
        repairRange = IntSetting("RepairRange");
        repairMinAltitude = FloatSetting("RepairMinAltitude");
        repairMaxAltitude = FloatSetting("RepairMaxAltitude");
        distToTargetToGrantRangeBonus = IntSetting("DistToTargetToGrantRangeBonus");
        repairRatePerSecond = FloatSetting("RepairRatePerSecond");
        repairWhenHealthBelowPercentage = IntSetting("RepairWhenBelowHealth%");
        minReadyFrames = FramesSetting("RepairMinReadyTime");
        maxReadyFrames = FramesSetting("RepairMaxReadyTime");
        minWeldFrames = FramesSetting("RepairMinWeldTime");
        maxWeldFrames = FramesSetting("RepairMaxWeldTime");
        weldingSysName = Definition.Get("RepairWeldingSys") ?? "";
        weldingFxBone = Definition.Get("RepairWeldingFXBone") ?? "";
        stayOnSameLayerAsMaster = BoolSetting("StayOnSameLayerAsMaster");
    }

    public void OnCreate()
    {
        if (repairRatePerSecond > 0)
            Owner.SetCondition("PACKING");
    }

    public void OnBuildComplete() { }

    public void OnEnslave(GameObject master) => StartSlavedEffects(master);

    public void OnSlaverDie(DamageInfo damage) => StopSlavedEffects();

    // (The original makes slaves go prone when their master is hit; ZH infantry have no prone state.)
    public void OnSlaverDamage(DamageInfo damage) { }

    public uint SlaverId => slaver;

    public int Update()
    {
        if (framesToWait > 0)
            framesToWait--;
        if (repairState == RepairState.None)
        {
            if (framesToWait > 0)
                return 0;
            framesToWait = SlavedUpdateRate;
        }
        if (slaver == 0)
            return 0;
        var me = Owner;
        if (me.AI is not { } myAI || myAI.CurrentLocomotor == null)
            return 0;
        var master = Game.FindObject(slaver);
        if (master == null || master.IsEffectivelyDead || master.IsDisabledBy("DISABLED_UNMANNED"))
        {
            StopSlavedEffects();
            me.SetDisabled("DISABLED_UNMANNED");
            myAI.Stop(Commands.CommandSource.AI);
            return 0;
        }
        if (master.Team != null && me.Team != null && master.Team.RelationshipTo(me.Team) != Players.Relationship.Allies)
            me.Defect(master.Team, 0);

        if (stayOnSameLayerAsMaster)
            me.Layer = master.Layer;
        master.SetWeaponBonus("DRONE_SPOTTING", false);

        var masterAI = master.AI;
        var target = masterAI?.CurrentVictim;

        int healthPercentage = 100;
        if (repairRatePerSecond > 0 && master.MaxHealth > 0)
            healthPercentage = (int)(master.Health / master.MaxHealth * 100f);
        if (healthPercentage <= repairWhenHealthBelowPercentage)
        {
            DoRepairLogic(master);
            return 0;
        }
        if (attackRange != 0 && target != null)
        {
            EndRepair();
            DoAttackLogic(master, target);
            return 0;
        }
        if (scoutRange != 0 && masterAI != null && masterAI.Path.Count > 0)
        {
            var masterDest = masterAI.Path[^1];
            float distSqr = DistanceSqrFromBoundingSphere2D(master, masterDest);
            if (distSqr > (guardMaxRange * 0.5f) * (guardMaxRange * 0.5f))
            {
                EndRepair();
                DoScoutLogic(master, masterDest);
                return 0;
            }
        }
        if (healthPercentage < 100)
        {
            DoRepairLogic(master);
            return 0;
        }

        var pinned = master.Position;
        pinned.X += guardPointOffset.X;
        pinned.Y += guardPointOffset.Y;
        guardPointOffset.Z = Game.Terrain.GroundHeight(pinned.X, pinned.Y);
        if (guardMaxRange != 0)
        {
            if (myAI.IsIdle && Vector3.DistanceSquared(me.Position, pinned) > CloseEnoughSqr)
            {
                EndRepair();
                DoGuardLogic(pinned);
            }
            else if (Vector3.DistanceSquared(me.Position, master.Position) > MathF.Pow(StrayMultiplier * guardMaxRange, 2))
            {
                EndRepair();
                DoGuardLogic(pinned);
            }
        }
        return 0;
    }

    static float DistanceSqrFromBoundingSphere2D(GameObject obj, Vector3 point)
    {
        float d = MathF.Max(0, MathUtil.Distance2D(obj.Position, point) - obj.BoundingCircleRadius);
        return d * d;
    }

    Vector3 RandomOffset(float range)
    {
        float direction = Game.Random.Float(0, MathF.Tau);
        return new Vector3(range * MathF.Cos(direction), range * MathF.Sin(direction), 0);
    }

    void DoAttackLogic(GameObject master, GameObject target)
    {
        var targetPos = target.Position;
        float dist = DistanceSqrFromBoundingSphere2D(Owner, targetPos);
        Vector3 attackPosition;
        if (dist > attackRange * attackRange)
        {
            var vector = targetPos - master.Position;
            vector = vector.LengthSquared() > 0 ? Vector3.Normalize(vector) * attackRange : Vector3.Zero;
            attackPosition = master.Position + vector;
        }
        else
            attackPosition = targetPos;
        if (attackWanderRange != 0)
        {
            guardPointOffset = RandomOffset(attackWanderRange);
            attackPosition.X += guardPointOffset.X;
            attackPosition.Y += guardPointOffset.Y;
            guardPointOffset.Z = Game.Terrain.GroundHeight(attackPosition.X, attackPosition.Y);
        }
        Owner.AI?.MoveTo(attackPosition, Commands.CommandSource.AI);
        if (dist < distToTargetToGrantRangeBonus * distToTargetToGrantRangeBonus)
            master.SetWeaponBonus("DRONE_SPOTTING");
    }

    void DoScoutLogic(GameObject master, Vector3 mastersDestination)
    {
        float dist = DistanceSqrFromBoundingSphere2D(Owner, mastersDestination);
        Vector3 scoutPosition;
        if (dist > scoutRange * scoutRange)
        {
            var vector = mastersDestination - master.Position;
            vector = vector.LengthSquared() > 0 ? Vector3.Normalize(vector) * scoutRange : Vector3.Zero;
            scoutPosition = master.Position + vector;
        }
        else
            scoutPosition = mastersDestination;
        if (scoutWanderRange != 0)
        {
            guardPointOffset = RandomOffset(scoutWanderRange);
            scoutPosition.X += guardPointOffset.X;
            scoutPosition.Y += guardPointOffset.Y;
            guardPointOffset.Z = Game.Terrain.GroundHeight(scoutPosition.X, scoutPosition.Y);
        }
        Owner.AI?.MoveTo(scoutPosition, Commands.CommandSource.AI);
    }

    void DoGuardLogic(Vector3 pinned)
    {
        if (guardWanderRange != 0)
        {
            // As in the original, the new offset is GuardMaxRange long (not GuardWanderRange).
            guardPointOffset = RandomOffset(guardMaxRange);
            pinned.X += guardPointOffset.X;
            pinned.Y += guardPointOffset.Y;
            guardPointOffset.Z = Game.Terrain.GroundHeight(pinned.X, pinned.Y);
        }
        Owner.AI?.MoveTo(pinned, Commands.CommandSource.AI);
    }

    void DoRepairLogic(GameObject master)
    {
        if (Owner.AI is not { } ai)
            return;
        float distSqr = DistanceSqrFromBoundingSphere2D(Owner, master.Position);
        bool closeEnough = distSqr < 12f * 12f;
        if (closeEnough)
        {
            switch (repairState)
            {
                case RepairState.None:
                    SetRepairState(RepairState.Ready, master);
                    break;
                case RepairState.Ready:
                case RepairState.Extending:
                    if (framesToWait == 0)
                        SetRepairState(RepairState.Welding, master);
                    break;
                case RepairState.Unpacking:
                case RepairState.Welding:
                case RepairState.Retracting:
                    if (framesToWait == 0)
                        SetRepairState(RepairState.Ready, master);
                    break;
            }
        }
        else
        {
            repairing = false;
            bool closeEnoughForZPrecision = distSqr < MathF.Pow(master.BoundingSphereRadius * 2, 2);
            if (ai.CurrentLocomotor is { } locomotor)
                locomotor.UsePreciseZPos = closeEnoughForZPrecision;
            var pos = master.Position;
            pos.Z += Game.Random.Float(repairMinAltitude, repairMaxAltitude);
            ai.MoveTo(pos, Commands.CommandSource.AI);
            if (framesToWait == 0)
                SetRepairState(RepairState.Ready, master);
        }
        if (closeEnough && repairing)
            master.Body?.AttemptHealing(repairRatePerSecond / LogicTime.FramesPerSecond);
    }

    void EndRepair()
    {
        if (repairState != RepairState.None)
        {
            repairState = RepairState.None;
            framesToWait = SlavedUpdateRate;
            repairing = false;
            SetRepairModelConditionStates("PACKING");
        }
        if (Owner.AI is { } ai)
        {
            ai.SetLocomotorSet("SET_NORMAL");
            if (ai.CurrentLocomotor is { } locomotor)
            {
                locomotor.UltraAccurate = false;
                locomotor.UsePreciseZPos = false;
            }
        }
    }

    static readonly string[] RepairConditions =
        ["PACKING", "UNPACKING", "FIRING_B", "FIRING_C", "BETWEEN_FIRING_SHOTS_B", "BETWEEN_FIRING_SHOTS_C", "RELOADING_B", "RELOADING_C"];

    void SetRepairModelConditionStates(string flag)
    {
        foreach (string condition in RepairConditions)
            Owner.SetCondition(condition, false);
        Owner.SetCondition(flag);
    }

    void SetRepairState(RepairState state, GameObject master)
    {
        if (state == repairState)
            return;
        switch (state)
        {
            case RepairState.Unpacking:
                SetRepairModelConditionStates("UNPACKING");
                framesToWait = 15;
                break;
            case RepairState.Packing:
                SetRepairModelConditionStates("PACKING");
                framesToWait = 15;
                break;
            case RepairState.Ready:
                switch (repairState)
                {
                    case RepairState.None:
                        SetRepairModelConditionStates("UNPACKING");
                        repairState = RepairState.Unpacking;
                        framesToWait = 15;
                        break;
                    case RepairState.Welding:
                        repairState = RepairState.Retracting;
                        framesToWait = 5;
                        SetRepairModelConditionStates("FIRING_C");
                        MoveToNewRepairSpot(master);
                        break;
                    default:
                        repairState = RepairState.Ready;
                        framesToWait = Game.Random.Int(minReadyFrames, maxReadyFrames);
                        break;
                }
                break;
            case RepairState.Welding:
                if (repairState == RepairState.Ready)
                {
                    repairState = RepairState.Extending;
                    framesToWait = 5;
                    SetRepairModelConditionStates("FIRING_B");
                    break;
                }
                repairState = RepairState.Welding;
                framesToWait = Game.Random.Int(minWeldFrames, maxWeldFrames);
                if (weldingSysName.Length > 0)
                {
                    var pos = weldingFxBone.Length > 0 && Game.Bones.TryGetBone(Owner, weldingFxBone, out var bone) ? bone.Translation : Owner.Position;
                    int system = Game.Effects.CreateParticleSystem(weldingSysName, pos);
                    if (system != 0)
                        weldingSystems.Add((system, Frame + framesToWait));
                    Game.Events.PlaySound(Game.Catalog.MiscAudio("RepairSparks"), pos);
                }
                repairing = true;
                break;
        }
    }

    void MoveToNewRepairSpot(GameObject master)
    {
        weldingSystems.RemoveAll(w =>
        {
            if (w.EndFrame > Frame)
                return false;
            Game.Effects.DestroyParticleSystem(w.Id);
            return true;
        });
        if (repairRange == 0 || Owner.AI is not { } ai)
            return;
        float direction = Game.Random.Float(0, MathF.Tau);
        guardPointOffset = master.Position + new Vector3(repairRange * MathF.Cos(direction), repairRange * MathF.Sin(direction), 0);
        guardPointOffset.Z = Game.Terrain.GroundHeight(guardPointOffset.X, guardPointOffset.Y) + Game.Random.Float(repairMinAltitude, repairMaxAltitude);
        ai.SetLocomotorSet("SET_PANIC");
        if (ai.CurrentLocomotor is { } locomotor)
        {
            locomotor.UltraAccurate = true;
            locomotor.UsePreciseZPos = true;
        }
        ai.MoveTo(guardPointOffset, Commands.CommandSource.AI);
    }

    void StartSlavedEffects(GameObject master)
    {
        slaver = master.Id;
        Owner.ProducerId = master.Id;
        guardPointOffset = RandomOffset(guardMaxRange);
        Owner.SetStatus(Status.Unselectable);
        if (master.HasStatus(Status.Stealthed) && Owner.FindModule<Stealth.StealthUpdate>() is { } stealth)
            stealth.ReceiveGrant(true);
    }

    void StopSlavedEffects()
    {
        slaver = 0;
        guardPointOffset = Vector3.Zero;
        Owner.SetStatus(Status.Unselectable, false);
        Owner.SetDisabled("DISABLED_HELD", false);
    }
}

/// <summary>Keeps a floating object on the water surface (boats, floating debris).</summary>
public sealed class FloatUpdate : Module, IUpdateModule
{
    bool enabled;

    protected override void Initialize() => enabled = BoolSetting("Enabled", true);

    public int Update()
    {
        if (!enabled)
            return 1000;
        var position = Owner.Position;
        if (Game.Terrain.IsUnderwater(position.X, position.Y, out float waterHeight) && MathF.Abs(position.Z - waterHeight) > 0.01f)
            Owner.SetPosition(position with { Z = waterHeight });
        return 3;
    }
}
