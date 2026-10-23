using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules.SpecialPowers;

/// <summary>
/// The USA Strategy Center's battle plans (the original's BattlePlanUpdate): choosing Bombardment,
/// Hold the Line or Search and Destroy (OPTION_ONE/TWO/THREE of its power) packs up the current plan,
/// opens that plan's doors, and then boosts the whole army: bombardment weapons, HoldTheLine armor
/// (and the center's own health), SearchAndDestroy sight (and the center's stealth detection).
/// Changing plans paralyzes the army for BattlePlanChangeParalyzeTime.
/// </summary>
public sealed class BattlePlanUpdate : Module, IUpdateModule, ICreateModule, IOwnershipModule, ISpecialPowerUpdate
{
    enum TransitionStatus { Idle, Unpacking, Active, Packing }

    const uint OptionOne = 1u << 13, OptionTwo = 1u << 14, OptionThree = 1u << 15;

    TransitionStatus status = TransitionStatus.Idle;
    BattlePlanStatus currentPlan = BattlePlanStatus.None, desiredPlan = BattlePlanStatus.None, planAffectingArmy = BattlePlanStatus.None;
    int nextReadyFrame;
    bool invalidSettings, centeringTurret;
    readonly BattlePlanBonuses bonuses = new();
    uint visionObjectId;
    SpecialPowerModule? specialPowerModule;
    uint searchAndDestroyIdleSound;

    static readonly string[] planNames = ["", "Bombardment", "HoldTheLine", "SearchAndDestroy"];

    protected override void Initialize()
    {
        bonuses.ValidKindOf = Flags.KindOf.Parse(Setting("ValidMemberKindOf"));
        bonuses.InvalidKindOf = Flags.KindOf.Parse(Setting("InvalidMemberKindOf"));
    }

    public void OnCreate()
    {
        var obj = Owner;
        var power = Game.Catalog.SpecialPower(Setting("SpecialPowerTemplate"));
        if (power == null)
        {
            Log.Warning($"{obj.Template.Name}'s BattlePlanUpdate has no SpecialPowerTemplate.");
            invalidSettings = true;
            return;
        }
        specialPowerModule = SpecialPowerRules.ModuleFor(obj, power);
        obj.SetWeaponSetFlag("VETERAN");
        if (obj.AI != null)
            obj.Weapons.LockSlot(Combat.WeaponSlot.Primary);
        EnableTurret(false);
    }

    public bool InitiateIntentToDoSpecialPower(SpecialPowerTemplate power, GameObject? target, Vector3? position, uint options)
    {
        if (specialPowerModule?.Power != power)
            return false;
        if ((options & OptionOne) != 0)
            desiredPlan = BattlePlanStatus.Bombardment;
        else if ((options & OptionTwo) != 0)
            desiredPlan = BattlePlanStatus.HoldTheLine;
        else if ((options & OptionThree) != 0)
            desiredPlan = BattlePlanStatus.SearchAndDestroy;
        else
            return false;
        return true;
    }

    /// <summary>The plan the player picked (its button shows checked).</summary>
    public BattlePlanStatus DesiredPlan => desiredPlan;

    /// <summary>The plan in force (the original's getActiveBattlePlan).</summary>
    public BattlePlanStatus ActiveBattlePlan => status == TransitionStatus.Active ? planAffectingArmy : BattlePlanStatus.None;

    public int Update()
    {
        if (invalidSettings)
            return 0;
        if (nextReadyFrame > Frame)
            return 0;
        switch (status)
        {
            case TransitionStatus.Idle:
                if (desiredPlan != BattlePlanStatus.None)
                {
                    currentPlan = desiredPlan;
                    SetStatus(TransitionStatus.Unpacking);
                }
                break;
            case TransitionStatus.Unpacking:
                SetStatus(TransitionStatus.Active);
                if (currentPlan == BattlePlanStatus.Bombardment)
                    EnableTurret(true);
                break;
            case TransitionStatus.Active:
                if (currentPlan != desiredPlan)
                {
                    if (currentPlan == BattlePlanStatus.Bombardment)
                    {
                        if (Owner.AI is { } ai)
                        {
                            if (IsTurretInNaturalPosition())
                            {
                                SetStatus(TransitionStatus.Packing);
                                centeringTurret = false;
                                EnableTurret(false);
                            }
                            else if (!centeringTurret)
                            {
                                ai.Stop(Commands.CommandSource.AI);
                                Owner.AI.TurretForCurrentWeapon?.Recenter();
                                centeringTurret = true;
                            }
                        }
                    }
                    else
                        SetStatus(TransitionStatus.Packing);
                }
                break;
            case TransitionStatus.Packing:
                SetStatus(TransitionStatus.Idle);
                break;
        }
        return 0;
    }

    int AnimationFrames(BattlePlanStatus plan) => plan == BattlePlanStatus.None ? 0 : FramesSetting($"{planNames[(int)plan]}PlanAnimationTime");

    static string Door(BattlePlanStatus plan) => plan switch
    {
        BattlePlanStatus.Bombardment => "DOOR_1",
        BattlePlanStatus.HoldTheLine => "DOOR_2",
        _ => "DOOR_3",
    };

    void SetStatus(TransitionStatus newStatus)
    {
        var obj = Owner;
        if (status == newStatus)
            return;
        if (currentPlan != BattlePlanStatus.None)
        {
            string door = Door(currentPlan);
            switch (status)
            {
                case TransitionStatus.Unpacking: obj.SetCondition(door + "_OPENING", false); break;
                case TransitionStatus.Active:
                    obj.SetCondition(door + "_WAITING_TO_CLOSE", false);
                    if (currentPlan == BattlePlanStatus.SearchAndDestroy)
                        Game.Events.StopHandledSound(searchAndDestroyIdleSound);
                    break;
                case TransitionStatus.Packing: obj.SetCondition(door + "_CLOSING", false); break;
            }
        }

        int now = Frame;
        switch (newStatus)
        {
            case TransitionStatus.Idle:
                currentPlan = BattlePlanStatus.None;
                nextReadyFrame = now + FramesSetting("TransitionIdleTime");
                break;
            case TransitionStatus.Unpacking:
            {
                Game.Events.RadarEvent("BATTLE_PLAN", obj.Position, obj.Owner);
                if (currentPlan == BattlePlanStatus.None)
                    break;
                string name = planNames[(int)currentPlan];
                obj.SetCondition(Door(currentPlan) + "_OPENING");
                nextReadyFrame = now + AnimationFrames(currentPlan);
                Game.Events.PlaySound(Setting($"{name}PlanUnpackSoundName"), obj.Position, source: obj);
                if (obj.Owner.IsLocalPlayer)
                    Game.Events.Message(Setting($"{name}MessageLabel"), obj.Owner);
                Game.Events.PlaySound(Setting($"{name}AnnouncementName"), obj.Position);
                break;
            }
            case TransitionStatus.Active:
                SetBattlePlan(currentPlan);
                if (currentPlan != BattlePlanStatus.None)
                    obj.SetCondition(Door(currentPlan) + "_WAITING_TO_CLOSE");
                if (currentPlan == BattlePlanStatus.SearchAndDestroy)
                    searchAndDestroyIdleSound = Game.Events.PlayHandledSound(Setting("SearchAndDestroyPlanIdleLoopSoundName"), obj);
                break;
            case TransitionStatus.Packing:
            {
                SetBattlePlan(BattlePlanStatus.None);
                if (currentPlan == BattlePlanStatus.None)
                    break;
                string name = planNames[(int)currentPlan];
                obj.SetCondition(Door(currentPlan) + "_CLOSING");
                nextReadyFrame = now + AnimationFrames(currentPlan);
                // (The original checks the unpack sound's name before playing the pack sound.)
                if (Setting($"{name}PlanUnpackSoundName").Length > 0)
                    Game.Events.PlaySound(Setting($"{name}PlanPackSoundName"), obj.Position, source: obj);
                break;
            }
        }
        status = newStatus;
    }

    void EnableTurret(bool enable)
    {
        if (Owner.AI?.TurretForCurrentWeapon is { } turret)
            turret.Enabled = enable;
    }

    bool IsTurretInNaturalPosition() => Owner.AI?.TurretForCurrentWeapon is { IsInNaturalPosition: true };

    void ParalyzeTroops()
    {
        int frames = FramesSetting("BattlePlanChangeParalyzeTime");
        foreach (var obj in Game.ObjectsOwnedBy(Owner.Owner).ToList())
            if (obj.KindOf.HasAny(bonuses.ValidKindOf) && !obj.KindOf.HasAny(bonuses.InvalidKindOf))
                obj.SetDisabledFor("DISABLED_PARALYZED", frames);
    }

    void SetStealthDetection(bool on)
    {
        if (Owner.FindModule<Stealth.StealthDetectorUpdate>() is { } detector)
            detector.SetEnabled(on);
    }

    void SetBattlePlan(BattlePlanStatus plan)
    {
        var obj = Owner;
        var player = obj.Owner;
        float healthScalar = FloatSetting("StrategyCenterHoldTheLineMaxHealthScalar", 1);
        var healthChange = Setting("StrategyCenterHoldTheLineMaxHealthChangeType").ToUpperInvariant() switch
        {
            "SAME_CURRENTHEALTH" => Bodies.MaxHealthChange.SameCurrentHealth,
            "ADD_CURRENT_HEALTH_TOO" => Bodies.MaxHealthChange.AddCurrentHealthToo,
            "FULLY_HEAL" => Bodies.MaxHealthChange.FullyHeal,
            _ => Bodies.MaxHealthChange.PreserveRatio,
        };
        float sightScalar = FloatSetting("StrategyCenterSearchAndDestroySightRangeScalar", 1);
        bool detectsStealth = BoolSetting("StrategyCenterSearchAndDestroyDetectsStealth", true);

        // Take back the plan in force.
        switch (planAffectingArmy)
        {
            case BattlePlanStatus.Bombardment:
                player.ChangeBattlePlan(BattlePlanStatus.Bombardment, -1, bonuses);
                break;
            case BattlePlanStatus.HoldTheLine:
                player.ChangeBattlePlan(BattlePlanStatus.HoldTheLine, -1, bonuses);
                if (healthScalar != 1 && obj.Body is { } body)
                    body.SetMaxHealth(body.MaxHealth / healthScalar, healthChange);
                break;
            case BattlePlanStatus.SearchAndDestroy:
                player.ChangeBattlePlan(BattlePlanStatus.SearchAndDestroy, -1, bonuses);
                if (sightScalar != 1)
                {
                    obj.VisionRange /= sightScalar;
                    obj.ShroudClearingRange /= sightScalar;
                }
                if (detectsStealth)
                    SetStealthDetection(false);
                break;
        }
        bonuses.ArmorScalar = 1;
        bonuses.SightRangeScalar = 1;
        bonuses.Bombardment = bonuses.SearchAndDestroy = bonuses.HoldTheLine = 0;

        switch (plan)
        {
            case BattlePlanStatus.None:
                ParalyzeTroops();
                break;
            case BattlePlanStatus.Bombardment:
                bonuses.Bombardment = 1;
                player.ChangeBattlePlan(BattlePlanStatus.Bombardment, 1, bonuses);
                break;
            case BattlePlanStatus.HoldTheLine:
                if (healthScalar != 0 && obj.Body is { } body)
                    body.SetMaxHealth(body.MaxHealth * healthScalar, healthChange);
                bonuses.ArmorScalar = FloatSetting("HoldTheLinePlanArmorDamageScalar", 1);
                bonuses.HoldTheLine = 1;
                player.ChangeBattlePlan(BattlePlanStatus.HoldTheLine, 1, bonuses);
                break;
            case BattlePlanStatus.SearchAndDestroy:
                if (sightScalar != 1)
                {
                    obj.VisionRange *= sightScalar;
                    obj.ShroudClearingRange *= sightScalar;
                }
                if (detectsStealth)
                    SetStealthDetection(true);
                bonuses.SearchAndDestroy = 1;
                bonuses.SightRangeScalar = FloatSetting("SearchAndDestroyPlanSightRangeScalar", 1);
                player.ChangeBattlePlan(BattlePlanStatus.SearchAndDestroy, 1, bonuses);
                break;
        }
        planAffectingArmy = plan;
    }

    public void OnDelete()
    {
        if (visionObjectId != 0 && Game.FindObject(visionObjectId) is { } vision)
            Game.DestroyObject(vision);
        if (planAffectingArmy != BattlePlanStatus.None)
            Owner.Owner.ChangeBattlePlan(planAffectingArmy, -1, bonuses);
        Game.Events.StopHandledSound(searchAndDestroyIdleSound);
    }
}
