using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Contain;
using OpenGZH.Logic.Modules.Stealth;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.SpecialPowers;

/// <summary>
/// A unit ability's special power module (e.g. Black Lotus's capture, Burton's charges): it only
/// checks the unit isn't disabled; the matching SpecialAbilityUpdate does the work.
/// </summary>
public sealed class SpecialAbility : SpecialPowerModule
{
    public override void DoSpecialPowerAtLocation(Vector3 location, float angle, uint options)
    {
        if (!Owner.IsDisabled)
            base.DoSpecialPowerAtLocation(location, angle, options);
    }

    public override void DoSpecialPowerAtObject(GameObject target, uint options)
    {
        if (!Owner.IsDisabled)
            base.DoSpecialPowerAtObject(target, options);
    }

    public override void DoSpecialPower(uint options)
    {
        if (!Owner.IsDisabled)
            base.DoSpecialPower(options);
    }
}

/// <summary>
/// Carries out a unit ability step by step, exactly like the original:
/// 1 APPROACH the target until within StartAbilityRange; 2 FACE it; 3 UNPACK (UnpackTime);
/// 4 PREPARE for PreparationTime (a hacker's data stream, a capture's flag raising); 5 TRIGGER the
/// effect; 6 PACK (PackTime); 7 FINISH (optionally fleeing FleeRangeAfterCompletion).
/// Persistent abilities (PersistentPrepTime) keep re-triggering until stopped, e.g. a building hack.
/// A no-target order of a charge ability detonates the charges already placed.
/// </summary>
public sealed class SpecialAbilityUpdate : Module, IUpdateModule, ISpecialPowerUpdate, ICreateModule
{
    const float HugeDistance = 10000000f;

    enum Packing { None, Packing, Unpacking, Packed, Unpacked }

    // Settings
    SpecialPowerTemplate? power;
    float startAbilityRange, abilityAbortRange, packUnpackVariation, fleeRange;
    int preparationFrames, persistentPrepFrames, packFrames, unpackFrames, preTriggerUnstealthFrames, effectDuration;
    int maxSpecialObjects, awardXp, skillPoints;
    bool skipPackingWithNoTarget, specialObjectsPersistent, specialObjectsPersistWhenOwnerDies, alwaysValidate;
    bool flipAfterPacking, flipAfterUnpacking, doCaptureFx, loseStealthOnTrigger, approachRequiresLos, needToFaceTarget, persistenceRequiresRecharge;
    string specialObjectName = "";

    // State
    bool active;
    int prepFrames, animFrames;
    uint targetId;
    Vector3 targetPos;
    bool noTargetCommand;
    Packing packing = Packing.None;
    bool facingInitiated, facingComplete, withinStartAbilityRange;
    float captureFlashPhase;
    bool doDisableFxParticles = true;
    readonly List<uint> specialObjects = new();

    public SpecialPowerTemplate? Power => power;
    public bool IsActive => active;
    public int SpecialObjectCount => specialObjects.Count;
    public int SpecialObjectMax => maxSpecialObjects;

    protected override void Initialize()
    {
        power = Game.Catalog.SpecialPower(Setting("SpecialPowerTemplate"));
        startAbilityRange = FloatSetting("StartAbilityRange", HugeDistance);
        abilityAbortRange = FloatSetting("AbilityAbortRange", HugeDistance);
        preparationFrames = FramesSetting("PreparationTime");
        persistentPrepFrames = FramesSetting("PersistentPrepTime");
        packFrames = FramesSetting("PackTime");
        unpackFrames = FramesSetting("UnpackTime");
        preTriggerUnstealthFrames = FramesSetting("PreTriggerUnstealthTime");
        skipPackingWithNoTarget = BoolSetting("SkipPackingWithNoTarget");
        packUnpackVariation = FloatSetting("PackUnpackVariationFactor");
        specialObjectName = Setting("SpecialObject");
        maxSpecialObjects = IntSetting("MaxSpecialObjects", 1);
        specialObjectsPersistent = BoolSetting("SpecialObjectsPersistent");
        effectDuration = FramesSetting("EffectDuration");
        specialObjectsPersistWhenOwnerDies = BoolSetting("SpecialObjectsPersistWhenOwnerDies");
        alwaysValidate = BoolSetting("AlwaysValidateSpecialObjects");
        flipAfterPacking = BoolSetting("FlipOwnerAfterPacking");
        flipAfterUnpacking = BoolSetting("FlipOwnerAfterUnpacking");
        fleeRange = FloatSetting("FleeRangeAfterCompletion");
        doCaptureFx = BoolSetting("DoCaptureFX");
        loseStealthOnTrigger = BoolSetting("LoseStealthOnTrigger");
        awardXp = IntSetting("AwardXPForTriggering");
        skillPoints = IntSetting("SkillPointsForTriggering", -1);
        approachRequiresLos = BoolSetting("ApproachRequiresLOS", true);
        needToFaceTarget = BoolSetting("NeedToFaceTarget", true);
        persistenceRequiresRecharge = BoolSetting("PersistenceRequiresRecharge");
    }

    public void OnCreate() => Owner.SleepUpdate(this);

    string Type => power?.Type ?? "";
    bool IsType(params string[] types) => power != null && power.IsType(types);
    bool IsPreparationComplete => prepFrames == 0;
    bool IsPersistent => persistentPrepFrames > 0;
    bool HasTargetPos => targetPos != Vector3.Zero;
    GameObject? Target => Game.FindObject(targetId);
    SpecialPowerModule? MyPowerModule => Owner.FindModules<SpecialPowerModule>().FirstOrDefault(m => m.Power == power);
    int SleepTime => active || alwaysValidate ? 0 : LogicTime.SleepForever;

    public bool IsPowerCurrentlyInUse => packing != Packing.None && withinStartAbilityRange;

    // ---- Starting ----

    public bool InitiateIntentToDoSpecialPower(SpecialPowerTemplate requested, GameObject? target, Vector3? position, uint options)
    {
        if (requested != power)
            return false;
        targetId = 0;
        targetPos = Vector3.Zero;
        prepFrames = 0;
        animFrames = 0;
        packing = Packing.Packed;
        facingInitiated = facingComplete = withinStartAbilityRange = false;
        ClearAnimationConditions();

        if (target != null)
            targetId = target.Id;
        else if (position is { } at)
            targetPos = at;

        if (Owner.AI is not { } ai)
            return false;
        ai.Stop(CommandSource.AI);
        noTargetCommand = target == null && position == null;
        if (unpackFrames == 0 || noTargetCommand && skipPackingWithNoTarget)
            packing = Packing.Unpacked;
        active = true;

        // Only one of these abilities runs at a time.
        string[] exclusive = ["SPECIAL_BLACKLOTUS_DISABLE_VEHICLE_HACK", "SPECIAL_BLACKLOTUS_STEAL_CASH_HACK", "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING",
            "SPECIAL_REMOTE_CHARGES", "SPECIAL_TIMED_CHARGES", "SPECIAL_INFANTRY_CAPTURE_BUILDING", "SPECIAL_BOOBY_TRAP"];
        foreach (var other in Owner.FindModules<SpecialAbilityUpdate>())
            if (other != this && other.power != null && other.power.IsType(exclusive))
                other.OnExit(false);

        Owner.WakeUpdate(this);
        return true;
    }

    // ---- The update ----

    public int Update()
    {
        ValidateSpecialObjects();
        if (Owner.IsEffectivelyDead)
        {
            OnExit(true);
            return SleepTime;
        }
        if (!active)
            return SleepTime;
        var ai = Owner.AI;
        if (ai == null || ai.LastCommandSource != CommandSource.AI)
        {
            OnExit(false);
            return SleepTime;
        }
        if (ai.IsMoving && IsPowerCurrentlyInUse && !facingInitiated && IsType("SPECIAL_INFANTRY_CAPTURE_BUILDING", "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING"))
        {
            OnExit(false);
            return SleepTime;
        }
        if (HandlePacking())
            return SleepTime;

        var spm = MyPowerModule;
        if (ShouldAbort() || spm == null)
        {
            ai.Stop(CommandSource.AI);
            OnExit(false);
            return SleepTime;
        }

        if (!IsPreparationComplete)
        {
            bool powerReady = true;
            if (IsPersistent && persistenceRequiresRecharge)
                powerReady = spm.IsReady && spm.ReadyFrame < Frame;
            if (powerReady)
                prepFrames--;
            if (IsPreparationComplete)
            {
                TriggerAbilityEffect();
                if (IsPersistent)
                {
                    prepFrames = persistentPrepFrames;
                    if (persistenceRequiresRecharge)
                        spm.StartPowerRecharge();
                }
                else
                {
                    EndPreparation();
                    if (NeedToPack) StartPacking(true); else FinishAbility();
                }
            }
            else if (!ContinuePreparation())
            {
                EndPreparation();
                if (NeedToPack) StartPacking(false); else FinishAbility();
            }
        }
        else if (IsWithinStartAbilityRange())
        {
            withinStartAbilityRange = true;
            if (!IsFacing() && NeedToFace)
            {
                StartFacing();
                return SleepTime;
            }
            if (NeedToUnpack)
            {
                StartUnpacking();
                return SleepTime;
            }
            if (packing == Packing.Unpacked)
            {
                StartPreparation();
                if (IsPreparationComplete)
                {
                    TriggerAbilityEffect();
                    if (IsPersistent && persistenceRequiresRecharge)
                    {
                        prepFrames = persistentPrepFrames;
                        spm.StartPowerRecharge();
                        return SleepTime;
                    }
                    EndPreparation();
                    if (NeedToPack) StartPacking(true); else FinishAbility();
                }
            }
        }
        else if (ai.IsIdle)
        {
            ApproachTarget();
        }
        return SleepTime;
    }

    bool ShouldAbort()
    {
        if (targetId == 0 || Target is not { } target)
            return false;
        if (target.IsEffectivelyDead)
            return true;
        bool hiddenStealthed = target.HasStatus(Status.Stealthed) && !target.HasStatus(Status.Detected);
        switch (Type)
        {
            // The original falls through from the capture/hack cases into the stealth check.
            case "SPECIAL_INFANTRY_CAPTURE_BUILDING":
            case "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING":
            case "SPECIAL_HACKER_DISABLE_BUILDING":
                if (target.Team == Owner.Team)
                    return true;
                return hiddenStealthed && !IsPreparationComplete;
            case "SPECIAL_BLACKLOTUS_STEAL_CASH_HACK":
            case "SPECIAL_BOOBY_TRAP":
                return hiddenStealthed && !IsPreparationComplete;
            case "SPECIAL_REMOTE_CHARGES":
            case "SPECIAL_TIMED_CHARGES":
                return !NeedToUnpack && hiddenStealthed && !IsPreparationComplete;
            case "SPECIAL_MISSILE_DEFENDER_LASER_GUIDED_MISSILES":
                return target.IsKindOf(Kind.Structure) || hiddenStealthed;
            case "SPECIAL_BLACKLOTUS_DISABLE_VEHICLE_HACK":
                return hiddenStealthed;
        }
        return false;
    }

    // ---- Packing ----

    void ClearAnimationConditions()
    {
        foreach (string condition in new[] { "UNPACKING", "PACKING", "FIRING_A", "RAISING_FLAG" })
            Owner.SetCondition(condition, false);
    }

    bool HandlePacking()
    {
        if (animFrames <= 0)
            return false;
        animFrames--;
        if (animFrames == 0)
        {
            Owner.SetCondition("UNPACKING", false);
            Owner.SetCondition("PACKING", false);
            if (packing == Packing.Unpacking)
            {
                if (flipAfterUnpacking)
                    Owner.SetOrientation(Owner.Orientation + MathF.PI);
                packing = Packing.Unpacked;
            }
            else if (packing == Packing.Packing)
            {
                if (flipAfterPacking)
                    Owner.SetOrientation(Owner.Orientation + MathF.PI);
                packing = Packing.Packed;
                FinishAbility();
                return true;
            }
            return false;
        }
        if (loseStealthOnTrigger && animFrames < preTriggerUnstealthFrames)
            Owner.FindModule<StealthUpdate>()?.MarkAsDetected();
        return true;
    }

    bool NeedToPack => packing == Packing.Unpacked && !(skipPackingWithNoTarget && noTargetCommand) && packFrames > 0;
    bool NeedToUnpack => packing == Packing.Packed && !(skipPackingWithNoTarget && noTargetCommand) && unpackFrames > 0;

    int Varied(int frames) => (int)(frames * Game.Random.Float(1 - packUnpackVariation, 1 + packUnpackVariation));

    void StartPacking(bool success)
    {
        packing = Packing.Packing;
        animFrames = Varied(packFrames);
        Owner.SetCondition("UNPACKING", false);
        Owner.SetCondition("RAISING_FLAG", false);
        Owner.SetCondition("PACKING");
        Game.Events.PlaySound(Setting("PackSound"), Owner.Position);
        Owner.AI?.SetBusy(CommandSource.AI);
        if (success)
        {
            string voice = Type switch
            {
                "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING" => Owner.Template.UnitSound("VoiceCaptureBuildingComplete"),
                "SPECIAL_BLACKLOTUS_DISABLE_VEHICLE_HACK" => Owner.Template.UnitSound("VoiceDisableVehicleComplete"),
                "SPECIAL_BLACKLOTUS_STEAL_CASH_HACK" => Owner.Template.UnitSound("VoiceStealCashComplete"),
                _ => Owner.Template.Rules.GetString("VoiceTaskComplete"),
            };
            Game.Events.PlaySound(voice, Owner.Position, Owner.Owner);
        }
    }

    void StartUnpacking()
    {
        packing = Packing.Unpacking;
        animFrames = Varied(unpackFrames);
        Owner.SetCondition("PACKING", false);
        Owner.SetCondition("UNPACKING");
        Game.Events.PlaySound(Setting("UnpackSound"), Owner.Position);
        Owner.AI?.SetBusy(CommandSource.AI);
    }

    // ---- Range and approach ----

    float DistanceSquaredToTarget(out bool hasTarget)
    {
        hasTarget = true;
        if (targetId != 0)
        {
            if (Target is not { } target)
                return 0;
            // FROM_BOUNDINGSPHERE_2D: centre distance less both radii.
            float d = MathF.Max(0, MathUtil.Distance2D(Owner.Position, target.Position) - Owner.BoundingCircleRadius - target.BoundingCircleRadius);
            return d * d;
        }
        if (HasTargetPos)
        {
            float d = MathF.Max(0, MathUtil.Distance2D(Owner.Position, targetPos) - Owner.BoundingCircleRadius);
            return d * d;
        }
        hasTarget = false;
        return 0;
    }

    bool IsTouchingTarget() => Target is { } target
        && MathUtil.Distance2D(Owner.Position, target.Position) <= Owner.BoundingCircleRadius + target.BoundingCircleRadius;

    bool IsWithinStartAbilityRange()
    {
        if (withinStartAbilityRange)
            return true;
        const float Undersize = Formats.Maps.MapFile.CellSize * 0.25f; // a quarter pathfinding cell
        float range = MathF.Max(0, startAbilityRange - Undersize);
        float distanceSquared = DistanceSquaredToTarget(out bool hasTarget);
        if (!hasTarget)
            return true;
        if (distanceSquared > startAbilityRange * startAbilityRange)
            return false;
        if (range == 0 && targetId != 0)
            return IsTouchingTarget();
        if (approachRequiresLos && Target is { } target)
            return Game.Terrain.HasLineOfSight(Owner, target);
        return true;
    }

    bool IsWithinAbilityAbortRange()
    {
        const float Undersize = Formats.Maps.MapFile.CellSize * 0.25f; // a quarter pathfinding cell
        float range = MathF.Max(0, startAbilityRange - Undersize);
        float distanceSquared = DistanceSquaredToTarget(out bool hasTarget);
        if (!hasTarget)
            return true;
        if (distanceSquared > abilityAbortRange * abilityAbortRange)
            return false;
        if (range == 0 && targetId != 0)
            return IsTouchingTarget();
        return true;
    }

    void ApproachTarget()
    {
        if (Owner.AI is not { } ai)
            return;
        if (targetId != 0 && Target is { } target)
            ai.MoveToObject(target, CommandSource.AI);
        else if (HasTargetPos)
            ai.MoveTo(targetPos, CommandSource.AI);
    }

    // ---- Facing ----

    bool IsFacing()
    {
        if (Owner.AI is not { } ai)
            return true;
        if (!facingComplete && facingInitiated)
        {
            if (ai.IsIdle)
            {
                facingComplete = true;
                return false;
            }
            return true;
        }
        return false;
    }

    bool NeedToFace => Owner.AI != null && needToFaceTarget && (!facingInitiated || !facingComplete);

    void StartFacing()
    {
        if (Owner.AI is not { } ai)
            return;
        ai.Stop(CommandSource.AI);
        facingInitiated = true;
        if (Target is { } target)
            ai.FaceObject(target, CommandSource.AI);
        else if (HasTargetPos)
            ai.FacePosition(targetPos, CommandSource.AI);
    }

    // ---- Preparation ----

    void StartPreparation()
    {
        prepFrames = preparationFrames;
        var target = Target;
        switch (Type)
        {
            case "SPECIAL_MISSILE_DEFENDER_LASER_GUIDED_MISSILES":
                if (target != null && CreateSpecialObject() is { } laser && !InitLaser(laser, target))
                    return;
                break;

            case "SPECIAL_INFANTRY_CAPTURE_BUILDING":
                if (target != null)
                {
                    if (target.Team == Owner.Team)
                        return;
                    if (target.CheckAndDetonateBoobyTrap(Owner) && target.IsEffectivelyDead)
                        return;
                }
                Owner.SetCondition("UNPACKING", false);
                Owner.SetCondition("RAISING_FLAG");
                if (target != null)
                {
                    Game.Events.Eva("BuildingBeingStolen", target.Owner);
                    Game.Events.RadarEvent("Infiltration", target.Position, target.Owner);
                }
                break;

            case "SPECIAL_HACKER_DISABLE_BUILDING":
            case "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING":
            case "SPECIAL_BLACKLOTUS_DISABLE_VEHICLE_HACK":
            case "SPECIAL_BLACKLOTUS_STEAL_CASH_HACK":
                if (target != null)
                {
                    if (Owner.Owner.IsAllyOf(target.Owner))
                        return;
                    if (CreateSpecialObject() is { } stream)
                    {
                        if (!InitLaser(stream, target))
                            return;
                        Owner.SetCondition("UNPACKING", false);
                        Owner.SetCondition("FIRING_A");
                    }
                    if (Type == "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING")
                        Game.Events.Eva("BuildingBeingStolen", target.Owner);
                    Game.Events.RadarEvent("Infiltration", target.Position, target.Owner);
                }
                break;
        }

        MyPowerModule?.TriggerSpecialPower(null); // no view object
        Owner.AI?.Stop(CommandSource.AI);
        Owner.SetStatus(Status.UsingAbility);
        Game.Events.PlaySound(Setting("PrepSoundLoop"), Owner.Position);
    }

    bool InitLaser(GameObject laser, GameObject? target)
    {
        if (laser.FindModule<LaserUpdate>() is not { } beam)
        {
            KillSpecialObjects();
            return false;
        }
        beam.Init(Owner, target, Setting("SpecialObjectAttachToBone"));
        return true;
    }

    bool ContinuePreparation()
    {
        if (abilityAbortRange < HugeDistance && !IsWithinAbilityAbortRange())
            return false;
        switch (Type)
        {
            case "SPECIAL_MISSILE_DEFENDER_LASER_GUIDED_MISSILES":
            case "SPECIAL_BLACKLOTUS_DISABLE_VEHICLE_HACK":
            {
                if (Target is not { } target || Owner.Owner.IsAllyOf(target.Owner))
                    return false;
                foreach (uint id in specialObjects)
                    if (Game.FindObject(id) is { } laser && !InitLaser(laser, target))
                        return false;
                break;
            }
            case "SPECIAL_INFANTRY_CAPTURE_BUILDING":
            case "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING":
            {
                if (Target is not { } target || Owner.Owner.IsAllyOf(target.Owner))
                    return false;
                if (doCaptureFx)
                {
                    // The building flashes in the capturer's colour, faster as the capture nears completion.
                    bool lastPhase = ((int)captureFlashPhase & 1) != 0;
                    float increment = 1f - prepFrames / (float)Math.Max(1, preparationFrames);
                    captureFlashPhase += increment / 3f;
                    bool thisPhase = ((int)captureFlashPhase & 1) != 0;
                    if (lastPhase && !thisPhase)
                    {
                        target.FlashColor = Owner.Owner.Color;
                        Game.Events.PlaySound("DefectorTimerTickSound", target.Position);
                    }
                }
                if (Type == "SPECIAL_INFANTRY_CAPTURE_BUILDING")
                    MyPowerModule?.StartPowerRecharge();
                break;
            }
        }
        return true;
    }

    void EndPreparation()
    {
        Owner.SetStatus(Status.UsingAbility, false);
        if (IsType("SPECIAL_MISSILE_DEFENDER_LASER_GUIDED_MISSILES", "SPECIAL_HACKER_DISABLE_BUILDING", "SPECIAL_BLACKLOTUS_DISABLE_VEHICLE_HACK",
                "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING", "SPECIAL_BLACKLOTUS_STEAL_CASH_HACK", "SPECIAL_INFANTRY_CAPTURE_BUILDING"))
            KillSpecialObjects();
    }

    // ---- The effect ----

    void TriggerAbilityEffect()
    {
        if (awardXp > 0)
            Owner.AddExperience(awardXp);
        int points = skillPoints != -1 ? skillPoints : awardXp;
        if (points > 0)
            Owner.Owner.AddSkillPoints(points);
        Game.Events.PlaySound(Setting("TriggerSound"), Owner.Position);

        bool okToLoseStealth = true;
        var target = Target;
        switch (Type)
        {
            case "SPECIAL_MISSILE_DEFENDER_LASER_GUIDED_MISSILES":
                if (target != null && Owner.Weapons.Slots[(int)WeaponSlot.Secondary] != null)
                {
                    Owner.Weapons.LockSlot(WeaponSlot.Secondary);
                    Owner.AI?.AttackObject(target, CommandSource.AI);
                }
                break;

            case "SPECIAL_HELIX_NAPALM_BOMB":
                CreateSpecialObject();
                break;

            case "SPECIAL_TANKHUNTER_TNT_ATTACK":
            case "SPECIAL_TIMED_CHARGES":
            case "SPECIAL_BOOBY_TRAP":
            {
                if (target == null)
                    return;
                if (target.CheckAndDetonateBoobyTrap(Owner) && (target.IsEffectivelyDead || Owner.IsEffectivelyDead))
                    return;
                if (Type == "SPECIAL_BOOBY_TRAP" && target.HasStatus(Status.BoobyTrapped))
                    return;
                if (CreateSpecialObject() is { } charge)
                {
                    if (charge.FindModule<StickyBombUpdate>() is not { } bomb)
                    {
                        Log.Warning($"{Owner.Template.Name}: {charge.Template.Name} needs a StickyBombUpdate.");
                        KillSpecialObjects();
                        return;
                    }
                    bomb.Init(target, Owner);
                }
                break;
            }

            case "SPECIAL_HACKER_DISABLE_BUILDING":
            case "SPECIAL_BLACKLOTUS_DISABLE_VEHICLE_HACK":
            {
                if (target == null || Owner.Owner.IsAllyOf(target.Owner))
                    return;
                target.SetDisabledFor("DISABLED_HACKED", effectDuration);
                float footprintArea = target.Geometry.FootprintArea;
                if (footprintArea < 300 && target.IsKindOf(Kind.Structure))
                {
                    doDisableFxParticles = !doDisableFxParticles; // small buildings get every other effect
                }
                if (doDisableFxParticles)
                    Game.Events.ParticleSystem(Setting("DisableFXParticleSystem"), target.Position, target);
                break;
            }

            case "SPECIAL_INFANTRY_CAPTURE_BUILDING":
            case "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING":
            {
                if (target == null)
                    return;
                if (target.CheckAndDetonateBoobyTrap(Owner) && (target.IsEffectivelyDead || Owner.IsEffectivelyDead))
                    return;
                if (target.Team == Owner.Team)
                    return;
                if (target.FindModule<ContainModule>() is { IsGarrisonable: true } garrison)
                {
                    // Capturing a garrisoned building throws the occupants out; it stays neutral.
                    garrison.EvacuateAll();
                    break;
                }
                Game.Events.Eva("BuildingStolen", target.Owner);
                target.Defect(Owner.Owner.DefaultTeam, 1);
                if (Type == "SPECIAL_BLACKLOTUS_CAPTURE_BUILDING")
                    MyPowerModule?.StartPowerRecharge();
                break;
            }

            case "SPECIAL_BLACKLOTUS_STEAL_CASH_HACK":
            {
                if (target == null)
                    return;
                int cash = Math.Min(1000, target.Owner.Money.Amount);
                if (cash > 0)
                {
                    target.Owner.Money.TryWithdraw(cash);
                    Owner.Owner.Money.Deposit(cash);
                    Owner.Owner.MoneyEarned += cash;
                    Game.Events.Eva("CashStolen", target.Owner);
                    Game.Events.MoneyEarned(Owner, cash);
                    Game.Events.MoneyEarned(target, -cash);
                }
                break;
            }

            case "SPECIAL_REMOTE_CHARGES":
            {
                if (target != null && target.CheckAndDetonateBoobyTrap(Owner) && (target.IsEffectivelyDead || Owner.IsEffectivelyDead))
                    return;
                if (targetId == 0 && !HasTargetPos)
                {
                    // The detonator: set off every charge placed so far.
                    foreach (uint id in specialObjects.ToList())
                    {
                        if (Game.FindObject(id)?.FindModule<StickyBombUpdate>() is { } bomb)
                        {
                            bomb.Detonate();
                            okToLoseStealth = false;
                        }
                    }
                }
                else
                {
                    if (target == null)
                        return;
                    if (CreateSpecialObject() is { } charge)
                    {
                        if (charge.FindModule<StickyBombUpdate>() is not { } bomb)
                        {
                            Log.Warning($"{Owner.Template.Name}: {charge.Template.Name} needs a StickyBombUpdate.");
                            KillSpecialObjects();
                            return;
                        }
                        bomb.Init(target, Owner);
                    }
                }
                break;
            }

            case "SPECIAL_DISGUISE_AS_VEHICLE":
                if (target != null)
                    Owner.FindModule<StealthUpdate>()?.DisguiseAs(target);
                break;
        }

        if (loseStealthOnTrigger && okToLoseStealth)
            Owner.FindModule<StealthUpdate>()?.MarkAsDetected();
    }

    // ---- Special objects (lasers, charges) ----

    GameObject? CreateSpecialObject()
    {
        if (specialObjects.Count == maxSpecialObjects)
        {
            if (specialObjectsPersistent)
                return null;
            KillSpecialObjects();
        }
        if (Game.CreateObject(specialObjectName, Owner.Owner, Owner.Position, Owner.Orientation, Owner.Team) is not { } obj)
            return null;
        specialObjects.Add(obj.Id);
        obj.ExperienceSinkId = Owner.Id;
        return obj;
    }

    void KillSpecialObjects()
    {
        foreach (uint id in specialObjects)
            if (Game.FindObject(id) is { } obj)
                Game.DestroyObject(obj);
        specialObjects.Clear();
        if (Type == "SPECIAL_MISSILE_DEFENDER_LASER_GUIDED_MISSILES" && Owner.Weapons.Slots[(int)WeaponSlot.Primary] != null)
            Owner.Weapons.LockSlot(WeaponSlot.Primary);
    }

    void ValidateSpecialObjects() => specialObjects.RemoveAll(id => Game.FindObject(id) is null or { IsDestroyed: true });

    /// <summary>A charge this unit already placed on <paramref name="target"/> (charges call their target their producer).</summary>
    public GameObject? FindSpecialObjectOn(GameObject target) =>
        specialObjects.Select(Game.FindObject).FirstOrDefault(o => o != null && o.ProducerId == target.Id);

    // ---- Finishing ----

    void FinishAbility()
    {
        withinStartAbilityRange = false;
        packing = Packing.None;
        bool hadTarget = HasTargetPos || targetId != 0;
        if (fleeRange > 0 && hadTarget && Owner.AI is { } ai)
        {
            var direction = MathUtil.Forward(Owner.Orientation) * fleeRange;
            var fleeTo = flipAfterPacking || flipAfterUnpacking ? Owner.Position + direction : Owner.Position - direction;

            // Don't run onto our own mines.
            var mine = Game.Partition.Query(fleeTo, fleeRange)
                .Where(o => o.IsKindOf("MINE") && o.Owner == Owner.Owner)
                .OrderBy(o => MathUtil.DistanceSquared2D(o.Position, fleeTo))
                .FirstOrDefault();
            if (mine != null)
            {
                var away = fleeTo - mine.Position;
                away.Z = 0;
                if (away.LengthSquared() > 0)
                    fleeTo = mine.Position + Vector3.Normalize(away) * fleeRange;
            }
            ai.MoveTo(fleeTo, CommandSource.AI);
        }
        else
        {
            Owner.AI?.Stop(CommandSource.AI);
        }
        OnExit(false);
    }

    public void OnExit(bool cleanup)
    {
        ClearAnimationConditions();
        Owner.SetStatus(Status.UsingAbility, false);
        EndPreparation();
        if (!specialObjectsPersistent || cleanup && !specialObjectsPersistWhenOwnerDies)
            KillSpecialObjects();
        active = false;
        withinStartAbilityRange = false;
        packing = Packing.None;
    }
}

/// <summary>
/// A charge stuck to a target (demo charges, TNT, booby traps): follows the target, pings every
/// second, and when it detonates (timer, detonator or trap) it dies so its death weapon goes off.
/// GeometryBasedDamageWeapon instead damages everything around the target, scaled to its size.
/// </summary>
public sealed class StickyBombUpdate : Module, IUpdateModule, ICreateModule
{
    uint targetId;
    int dieFrame;
    int nextPingFrame;

    public GameObject? Target => Game.FindObject(targetId);

    public void OnCreate()
    {
        Owner.SleepUpdate(this);
        // A charge fired as a projectile sticks to whatever its shooter was aiming at.
        if (Game.FindObject(Owner.ProducerId)?.AI is { } shooterAi && Game.FindObject(shooterAi.TargetId) is { } goal)
            Init(goal, null);
    }

    public void Init(GameObject? target, GameObject? bomber, Vector3? specificPosition = null)
    {
        targetId = target?.Id ?? 0;
        Owner.ProducerId = target?.Id ?? 0;
        if (Owner.FindModule<LifetimeUpdate>() is { } lifetime)
        {
            dieFrame = lifetime.DieFrame;
            int pings = (dieFrame - Frame) / LogicTime.FramesPerSecond;
            nextPingFrame = dieFrame - pings * LogicTime.FramesPerSecond;
        }
        else
        {
            dieFrame = 0;
            nextPingFrame = Frame + LogicTime.FramesPerSecond;
        }
        Owner.WakeUpdate(this);

        if (target == null)
            return;
        var position = target.Position;
        if (specificPosition is { } at)
            position = at with { Z = Game.Terrain.GroundHeight(at) };
        else if (target.IsKindOf(Kind.Immobile) && bomber != null)
            position = bomber.Position with { Z = Game.Terrain.GroundHeight(bomber.Position) };
        else
            position.Z += FloatSetting("OffsetZ"); // ride on the roof of the truck or tank
        Owner.SetPosition(position);
        if (Owner.IsKindOf("BOOBY_TRAP"))
            target.SetStatus(Status.BoobyTrapped);
        Game.Events.PlaySound(Owner.Template.UnitSound("StickyBombCreated"), Owner.Position);
    }

    public int Update()
    {
        if (Target is { } target)
        {
            if (target.IsEffectivelyDead)
            {
                Game.DestroyObject(Owner);
                return 0;
            }
            if (target.IsKindOf(Kind.Immobile))
                Owner.SetPosition(Owner.Position with { Z = Game.Terrain.GroundHeight(Owner.Position) });
            else
                Owner.SetPosition(target.Position + new Vector3(0, 0, FloatSetting("OffsetZ")));
        }
        if (Frame >= nextPingFrame)
        {
            nextPingFrame += LogicTime.FramesPerSecond;
            Game.Events.PlaySound(Owner.Template.UnitSound("UnitBombPing"), Owner.Position);
        }
        return 0;
    }

    public void Detonate()
    {
        var trapped = Target;
        if (Game.Catalog.Weapon(Setting("GeometryBasedDamageWeapon")) is { } weapon && trapped != null)
        {
            float boundingCircle = trapped.BoundingCircleRadius;
            float primaryRange = weapon.PrimaryDamageRadius + boundingCircle;
            float secondaryRange = weapon.SecondaryDamageRadius + boundingCircle;
            float radius = Math.Max(primaryRange, secondaryRange);
            foreach (var victim in Game.Partition.Query(trapped.Position, radius).ToList())
            {
                // FROM_BOUNDINGSPHERE_3D distance.
                float distance = MathF.Max(0, Vector3.Distance(victim.Position, trapped.Position) - victim.BoundingCircleRadius);
                if (distance > radius)
                    continue;
                victim.Damage(new DamageInfo
                {
                    Amount = distance <= primaryRange ? weapon.PrimaryDamage : weapon.SecondaryDamage,
                    DamageType = weapon.DamageType,
                    DeathType = weapon.DeathType,
                    DamageStatusType = weapon.DamageStatusType,
                    SourceId = Owner.Id,
                });
            }
            Game.Events.PlayFx(Setting("GeometryBasedDamageFX"), trapped.Position);
        }
        if (Owner.IsKindOf("BOOBY_TRAP") && trapped != null)
            trapped.SetStatus(Status.BoobyTrapped, false);
        Owner.Kill(); // the charge's death weapon does the rest
    }
}

/// <summary>
/// A beam between two objects (the Pathfinder's laser, a Hacker's data stream). The logic only
/// remembers its ends; the client draws it with the object's W3DLaserDraw.
/// </summary>
public sealed class LaserUpdate : Module
{
    public uint SourceId { get; private set; }
    public uint TargetId { get; private set; }
    public string SourceBone { get; private set; } = "";

    public void Init(GameObject source, GameObject? target, string sourceBone)
    {
        SourceId = source.Id;
        TargetId = target?.Id ?? 0;
        SourceBone = sourceBone;
    }
}
