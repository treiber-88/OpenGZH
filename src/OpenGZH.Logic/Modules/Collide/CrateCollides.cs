using OpenGZH.Core.Rules;
using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Commands;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.AI;
using OpenGZH.Logic.Modules.SpecialPowers;
using OpenGZH.Logic.Modules.Updates;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Collide;

/// <summary>
/// Something that does its effect on whatever touches it and then disappears: crates, but also
/// hijackers, terrorists and saboteurs, who are "crates" that touch their targets.
/// Who may pick it up: not neutral, a unit (or a building with BuildingPickup), RequiredKindOf and
/// not ForbiddenKindOf, alive, not a parachute; the crate must be on the ground; ForbidOwnerPlayer,
/// HumanOnly and PickupScience narrow it further. ExecuteFX plays on the picker.
/// </summary>
public abstract class CrateCollide : Module, ICollideModule
{
    FlagSet requiredKindOf, forbiddenKindOf;
    bool forbidOwnerPlayer, buildingPickup, humanOnly;
    string pickupScience = "", executeFx = "", executeAnimation = "";

    protected override void Initialize()
    {
        requiredKindOf = Flags.KindOf.Parse(Setting("RequiredKindOf"));
        forbiddenKindOf = Flags.KindOf.Parse(Setting("ForbiddenKindOf"));
        forbidOwnerPlayer = BoolSetting("ForbidOwnerPlayer");
        buildingPickup = BoolSetting("BuildingPickup");
        humanOnly = BoolSetting("HumanOnly");
        pickupScience = Setting("PickupScience");
        executeFx = Setting("ExecuteFX");
        executeAnimation = Setting("ExecuteAnimation");
        InitializeCrate();
    }

    protected virtual void InitializeCrate() { }

    public void OnCollide(GameObject? other, Vector3 location, Vector3 normal)
    {
        if (other == null || !IsValidToExecute(other))
            return;
        if (ExecuteCrateBehavior(other))
        {
            if (executeFx.Length > 0)
                Game.Events.PlayFx(executeFx, other.Position, other);
            Game.DestroyObject(Owner);
        }
        if (executeAnimation.Length > 0)
            Game.Events.WorldAnimation(executeAnimation, Owner.Position);
    }

    public virtual bool IsValidToExecute(GameObject other)
    {
        if (other.Owner == Game.Players.Neutral)
            return false;
        bool validBuilding = buildingPickup && other.IsKindOf(Kind.Structure);
        if (other.AI == null && !validBuilding)
            return false;
        if (!other.KindOf.HasAll(requiredKindOf) || (!forbiddenKindOf.IsEmpty && other.KindOf.HasAny(forbiddenKindOf)))
            return false;
        if (other.IsEffectivelyDead)
            return false;
        if (Owner.IsAboveTerrain && !validBuilding)
            return false;
        if (forbidOwnerPlayer && Owner.Owner == other.Owner)
            return false;
        if (humanOnly && !other.Owner.IsHuman)
            return false;
        if (pickupScience.Length > 0 && !other.Owner.HasScience(pickupScience))
            return false;
        if (other.IsKindOf("PARACHUTE"))
            return false;
        return true;
    }

    /// <summary>Does the effect; returns true to remove the crate.</summary>
    protected abstract bool ExecuteCrateBehavior(GameObject other);

    /// <summary>A unit-borne "crate" (hijacker, saboteur) only acts on the target it was sent to.</summary>
    protected bool IsGoalObject(GameObject other) =>
        Owner.AI is not { } ai || ai.EnterTargetId == other.Id || ai.TargetId == other.Id;

    protected enum SabotageVictim { FakeBuilding, CommandCenter, Superweapon, DropZone, SupplyCenter, InternetCenter, MilitaryFactory, PowerPlant }

    protected void SabotageFeedback(GameObject other, SabotageVictim victim)
    {
        string key = victim switch
        {
            SabotageVictim.FakeBuilding => "",
            SabotageVictim.CommandCenter or SabotageVictim.Superweapon => "SabotageResetTimerBuilding",
            SabotageVictim.DropZone or SabotageVictim.SupplyCenter => "MoneyWithdrawSound",
            _ => "SabotageShutDownBuilding",
        };
        if (key.Length == 0)
            return;
        Game.Events.PlaySound(Game.Catalog.MiscAudio(key), other.Position, source: other);
        Game.Events.FlashAsSelected(other);
    }

    protected void PlayMiscSound(string key, GameObject at) => Game.Events.PlaySound(Game.Catalog.MiscAudio(key), at.Position, source: at);

    protected bool IsEnemy(GameObject other) => Owner.Owner.IsEnemyOf(other.Owner);

    /// <summary>The original's isKindOfMulti for one KindOf name.</summary>
    protected static bool TemplateHasWeaponSet(GameObject obj, string flag) =>
        obj.Template.Rules.TryGet("WeaponSet", out var sets)
        && sets.AsList().Any(set => RuleValue.Words(set.GetString("Conditions")).Contains(flag, StringComparer.OrdinalIgnoreCase));
}

/// <summary>Money: MoneyProvided (plus UpgradedBoost "UpgradeName amount" with that upgrade).</summary>
public sealed class MoneyCrateCollide : CrateCollide
{
    int money;
    readonly List<(string Upgrade, int Amount)> boosts = new();

    protected override void InitializeCrate()
    {
        money = IntSetting("MoneyProvided");
        foreach (string line in ListSetting("UpgradedBoost"))
        {
            // "UpgradeType:Upgrade_AmericaSupplyLines Boost:25"
            var words = RuleValue.Words(line);
            string upgrade = words.FirstOrDefault(w => w.StartsWith("UpgradeType:", StringComparison.OrdinalIgnoreCase))?[12..] ?? (words.Length > 0 ? words[0] : "");
            string boost = words.FirstOrDefault(w => w.StartsWith("Boost:", StringComparison.OrdinalIgnoreCase))?[6..] ?? (words.Length > 1 ? words[1] : "0");
            boosts.Add((upgrade, RuleValue.Int(boost)));
        }
    }

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        int amount = money + boosts.Where(b => other.Owner.HasUpgrade(b.Upgrade)).Select(b => b.Amount).FirstOrDefault();
        other.Owner.Money.Deposit(amount);
        Game.Events.MoneyEarned(other, amount);
        PlayMiscSound("CrateMoney", other);
        return true;
    }
}

/// <summary>
/// Veterancy: one level (or as many as the crate's own level, AddsOwnerVeterancy) for the picker, or
/// for all its player's units within EffectRange. IsPilot: an ejected pilot climbing into one of its
/// own player's vehicles on the ground.
/// </summary>
public sealed class VeterancyCrateCollide : CrateCollide
{
    float range;
    bool addsOwnerVeterancy, isPilot;

    protected override void InitializeCrate()
    {
        range = IntSetting("EffectRange");
        addsOwnerVeterancy = BoolSetting("AddsOwnerVeterancy");
        isPilot = BoolSetting("IsPilot");
    }

    int LevelsToGain => addsOwnerVeterancy ? (int)Owner.Veterancy : 1;

    public override bool IsValidToExecute(GameObject other)
    {
        if (!base.IsValidToExecute(other) || other.IsSignificantlyAboveTerrain)
            return false;
        int levels = LevelsToGain;
        if (levels <= 0 || !other.IsTrainable || !other.CanGainExpForLevel(levels))
            return false;
        if (isPilot && (other.Owner != Owner.Owner || other.AI?.CurrentLocomotor?.Surfaces == Locomotion.Surfaces.Air))
            return false;
        return true;
    }

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        // As in the original: only when the crate's own AI is heading for it (a plain crate has no AI and never counts).
        if (Owner.AI is not { } ai || (ai.EnterTargetId != other.Id && ai.TargetId != other.Id))
            return false;
        int levels = LevelsToGain;
        if (range == 0)
            other.GainExpForLevel(levels, canScaleForBonus: !isPilot);
        else
            foreach (var obj in Game.Partition.Query(other.Position, range).Where(o => o.Owner == other.Owner && MathUtil.Distance2D(o.Position, other.Position) <= range))
                obj.GainExpForLevel(levels, canScaleForBonus: !isPilot);
        if (isPilot)
            Game.Scripts?.TransferObjectName(Owner, other);
        return true;
    }
}

/// <summary>Heals all of the picker's player's units and buildings.</summary>
public sealed class HealCrateCollide : CrateCollide
{
    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        foreach (var obj in Game.ObjectsOwnedBy(other.Owner).ToList())
            obj.Body?.SetHealth(obj.MaxHealth);
        PlayMiscSound("CrateHeal", other);
        return true;
    }
}

/// <summary>Free units: UnitCount of UnitName next to the picker.</summary>
public sealed class UnitCrateCollide : CrateCollide
{
    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        int count = IntSetting("UnitCount");
        string unit = Setting("UnitName");
        if (Game.Templates.Find(unit) == null)
            return false;
        for (int i = 0; i < count; i++)
        {
            var spot = Game.Pathfinder.NearestPassable(other.Position, Locomotion.Surfaces.Ground) ?? other.Position;
            if (MathUtil.Distance2D(spot, other.Position) > 20)
                spot = other.Position;
            Game.CreateObject(unit, other.Owner, spot with { Z = Game.Terrain.GroundHeight(spot) }, other.Orientation);
        }
        PlayMiscSound("CrateFreeUnit", other);
        return true;
    }
}

/// <summary>Reveals the whole map to the picker's player.</summary>
public sealed class ShroudCrateCollide : CrateCollide
{
    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        Game.Shroud.RevealMapForPlayer(other.Owner);
        PlayMiscSound("CrateShroud", other);
        return true;
    }
}

/// <summary>
/// GLA salvage: armor salvagers get the next armor crate upgrade, else weapon salvagers the next
/// weapon upgrade (WeaponChance), else a veterancy level (LevelChance), else MinMoney..MaxMoney.
/// </summary>
public sealed class SalvageCrateCollide : CrateCollide
{
    float weaponChance, levelChance;
    int minMoney, maxMoney;

    protected override void InitializeCrate()
    {
        weaponChance = FloatSetting("WeaponChance", 1);
        levelChance = FloatSetting("LevelChance", 0.25f);
        minMoney = IntSetting("MinMoney", 25);
        maxMoney = IntSetting("MaxMoney", 75);
    }

    public override bool IsValidToExecute(GameObject other) => base.IsValidToExecute(other) && other.IsKindOf("SALVAGER");

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        if (other.IsKindOf("ARMOR_SALVAGER") && !other.HasArmorSetFlag("CRATE_UPGRADE_TWO"))
        {
            if (other.HasArmorSetFlag("CRATE_UPGRADE_ONE"))
            {
                other.SetArmorSetFlag("CRATE_UPGRADE_ONE", false);
                other.SetArmorSetFlag("CRATE_UPGRADE_TWO");
                other.SetCondition("ARMORSET_CRATEUPGRADE_ONE", false);
                other.SetCondition("ARMORSET_CRATEUPGRADE_TWO");
            }
            else
            {
                other.SetArmorSetFlag("CRATE_UPGRADE_ONE");
                other.SetCondition("ARMORSET_CRATEUPGRADE_ONE");
            }
            PlayMiscSound("CrateSalvage", other);
        }
        else if (other.IsKindOf("WEAPON_SALVAGER") && !other.HasWeaponSetFlag("CRATEUPGRADE_TWO") && Chance(weaponChance))
        {
            if (other.HasWeaponSetFlag("CRATEUPGRADE_ONE"))
            {
                other.SetWeaponSetFlag("CRATEUPGRADE_ONE", false);
                other.SetWeaponSetFlag("CRATEUPGRADE_TWO");
            }
            else
                other.SetWeaponSetFlag("CRATEUPGRADE_ONE");
            PlayMiscSound("CrateSalvage", other);
        }
        else if (other.Veterancy != VeterancyLevel.Heroic && other.IsTrainable && Chance(levelChance))
        {
            other.GainExpForLevel(1, canScaleForBonus: true);
        }
        else
        {
            int money = minMoney != maxMoney ? Game.Random.Int(minMoney, maxMoney) : minMoney;
            if (money > 0)
            {
                other.Owner.Money.Deposit(money);
                Game.Events.MoneyEarned(Owner, money);
            }
            PlayMiscSound("CrateMoney", other);
        }
        return true;
    }

    bool Chance(float chance) => chance == 1f || Game.Random.Float(0, 1) < chance;
}

/// <summary>
/// A GLA Terrorist turning a vehicle into a car bomb: the vehicle (one with a CARBOMB weapon set)
/// joins the terrorist's side with his rank and arms itself, and the terrorist is used up.
/// </summary>
public sealed class ConvertToCarBombCrateCollide : CrateCollide
{
    public override bool IsValidToExecute(GameObject other)
    {
        if (!base.IsValidToExecute(other) || other.IsEffectivelyDead)
            return false;
        if (other.IsKindOf("AIRCRAFT") || other.IsKindOf("BOAT"))
            return false;
        if (other.HasStatus("IS_CARBOMB"))
            return false;
        if (!TemplateHasWeaponSet(other, "CARBOMB") || other.HasWeaponSetFlag("CARBOMB"))
            return false;
        return true;
    }

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        if (Owner.AI is { } ai && ai.EnterTargetId != other.Id && ai.TargetId != other.Id)
            return false;
        if (other.CheckAndDetonateBoobyTrap(Owner) && (other.IsEffectivelyDead || Owner.IsEffectivelyDead))
            return false;
        other.SetWeaponSetFlag("CARBOMB");
        if (Setting("FXList") is { Length: > 0 } fx)
            Game.Events.PlayFx(fx, other.Position, other);
        other.Defect(Owner.Owner.DefaultTeam, 0);
        Game.Scripts?.TransferObjectName(Owner, other);
        other.VisionRange = Owner.VisionRange;
        other.ShroudClearingRange = Owner.ShroudClearingRange;
        other.SetStatus("IS_CARBOMB");
        other.SetVeterancy(Owner.Veterancy);
        return true;
    }
}

/// <summary>
/// The GLA Hijacker taking over an enemy vehicle (not aircraft, boats, drones, capture-immune or
/// loaded transports). The vehicle joins his side, both take the higher rank, and he rides inside it
/// (HijackerUpdate) - jumping out again if it is destroyed - unless it has no pilot to throw out.
/// </summary>
public sealed class ConvertToHijackedVehicleCrateCollide : CrateCollide
{
    public override bool IsValidToExecute(GameObject other)
    {
        if (!base.IsValidToExecute(other) || other.IsEffectivelyDead)
            return false;
        if (other.IsKindOf("IMMUNE_TO_CAPTURE") || other.IsKindOf("AIRCRAFT") || other.IsKindOf("BOAT") || other.IsKindOf("DRONE"))
            return false;
        if (other.HasStatus("HIJACKED") || !IsEnemy(other))
            return false;
        if (other.IsKindOf("TRANSPORT") && other.Contain is { Count: > 0 })
            return false;
        return true;
    }

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        var self = Owner;
        if (self.AI is { } ai && ai.EnterTargetId != other.Id && ai.TargetId != other.Id)
            return false;

        Game.Events.RadarEvent("INFILTRATION", other.Position, other.Owner);
        Game.Events.Eva("VehicleStolen", other.Owner);
        other.SetTeam(self.Owner.DefaultTeam);
        other.SetStatus("HIJACKED");
        if (other.AI is { } targetAi)
        {
            targetAi.MoveTo(other.Position, CommandSource.AI);
            targetAi.Stop(CommandSource.AI);
            (targetAi as DozerAIUpdate)?.CancelAllTasks();
        }
        Game.Events.PlaySound("HijackDriver", self.Position, source: self);
        Game.Scripts?.TransferObjectName(self, other);
        var highest = (VeterancyLevel)Math.Max((int)other.Veterancy, (int)self.Veterancy);
        self.SetVeterancy(highest);
        other.SetVeterancy(highest);

        if (other.Template.FindModule("EjectPilotDie") == null)
        {
            Game.DestroyObject(self);
            return true;
        }
        if (self.FindModule<HijackerUpdate>() is { } hijacker)
        {
            hijacker.RideIn(other);
            self.SetStatus("NO_COLLISIONS");
            self.SetStatus("MASKED");
            self.SetStatus("UNSELECTABLE");
        }
        self.AI?.Stop(CommandSource.AI);
        other.VisionRange = self.VisionRange;
        other.ShroudClearingRange = self.ShroudClearingRange;
        self.IsRemovedFromPartition = true;
        self.DrawableHidden = true;
        return false;
    }
}

/// <summary>Saboteur on a supply center: steals up to StealCashAmount from its owner.</summary>
public sealed class SabotageSupplyCenterCrateCollide : CrateCollide
{
    public override bool IsValidToExecute(GameObject other) =>
        base.IsValidToExecute(other) && !other.IsEffectivelyDead && other.IsKindOf("FS_SUPPLY_CENTER") && IsEnemy(other);

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        if (!IsGoalObject(other))
            return false;
        Game.Events.RadarEvent("INFILTRATION", other.Position, other.Owner);
        SabotageFeedback(other, SabotageVictim.SupplyCenter);
        int cash = Math.Min(IntSetting("StealCashAmount"), other.Owner.Money.Amount);
        if (cash > 0)
        {
            other.Owner.Money.WithdrawUpTo(cash);
            Owner.Owner.Money.Deposit(cash);
            Game.Events.MoneyEarned(Owner, cash);
            Game.Events.MoneyLost(other, cash);
            Game.Events.Eva("CashStolen", other.Owner);
        }
        else
            Game.Events.Eva("BuildingSabotaged", other.Owner);
        return true;
    }
}

/// <summary>Saboteur on a power plant: the whole base loses power for SabotagePowerDuration.</summary>
public sealed class SabotagePowerPlantCrateCollide : CrateCollide
{
    public override bool IsValidToExecute(GameObject other) =>
        base.IsValidToExecute(other) && !other.IsEffectivelyDead && other.IsKindOf("FS_POWER") && IsEnemy(other);

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        if (!IsGoalObject(other))
            return false;
        Game.Events.RadarEvent("INFILTRATION", other.Position, other.Owner);
        SabotageFeedback(other, SabotageVictim.PowerPlant);
        Game.Events.Eva("BuildingSabotaged", other.Owner);
        other.Owner.Energy.SabotagedUntilFrame = Frame + FramesSetting("SabotagePowerDuration");
        other.Owner.UpdatePowerBrownOut();
        return true;
    }
}

/// <summary>Saboteur on a barracks, war factory or airfield: it stops working for SabotageDuration.</summary>
public sealed class SabotageMilitaryFactoryCrateCollide : CrateCollide
{
    public override bool IsValidToExecute(GameObject other) =>
        base.IsValidToExecute(other) && !other.IsEffectivelyDead && !other.IsKindOf("AIRCRAFT_CARRIER")
        && (other.IsKindOf("FS_BARRACKS") || other.IsKindOf("FS_WARFACTORY") || other.IsKindOf("FS_AIRFIELD")) && IsEnemy(other);

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        if (!IsGoalObject(other))
            return false;
        Game.Events.RadarEvent("INFILTRATION", other.Position, other.Owner);
        SabotageFeedback(other, SabotageVictim.MilitaryFactory);
        Game.Events.Eva("BuildingSabotaged", other.Owner);
        other.SetDisabledFor("DISABLED_HACKED", FramesSetting("SabotageDuration"));
        return true;
    }
}

/// <summary>Saboteur on a command center: its powers start recharging from zero.</summary>
public sealed class SabotageCommandCenterCrateCollide : CrateCollide
{
    public override bool IsValidToExecute(GameObject other) =>
        base.IsValidToExecute(other) && !other.IsEffectivelyDead && other.IsKindOf("COMMANDCENTER") && IsEnemy(other);

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        if (!IsGoalObject(other))
            return false;
        Game.Events.RadarEvent("INFILTRATION", other.Position, other.Owner);
        SabotageFeedback(other, SabotageVictim.CommandCenter);
        Game.Events.Eva("BuildingSabotaged", other.Owner);
        foreach (var power in other.FindModules<SpecialPowerModule>())
            power.StartPowerRecharge();
        return true;
    }
}

/// <summary>Saboteur on a superweapon or strategy center: its powers start recharging from zero.</summary>
public sealed class SabotageSuperweaponCrateCollide : CrateCollide
{
    public override bool IsValidToExecute(GameObject other) =>
        base.IsValidToExecute(other) && !other.IsEffectivelyDead && (other.IsKindOf("FS_SUPERWEAPON") || other.IsKindOf("FS_STRATEGY_CENTER")) && IsEnemy(other);

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        if (!IsGoalObject(other))
            return false;
        Game.Events.RadarEvent("INFILTRATION", other.Position, other.Owner);
        SabotageFeedback(other, SabotageVictim.Superweapon);
        Game.Events.Eva("BuildingSabotaged", other.Owner);
        foreach (var power in other.FindModules<SpecialPowerModule>())
            power.StartPowerRecharge();
        return true;
    }
}

/// <summary>
/// Saboteur on an Internet Center: it, its hackers and all its owner's internet centers' spy vision
/// are knocked out for SabotageDuration.
/// </summary>
public sealed class SabotageInternetCenterCrateCollide : CrateCollide
{
    public override bool IsValidToExecute(GameObject other) =>
        base.IsValidToExecute(other) && !other.IsEffectivelyDead && other.IsKindOf("FS_INTERNET_CENTER") && IsEnemy(other);

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        if (!IsGoalObject(other))
            return false;
        Game.Events.RadarEvent("INFILTRATION", other.Position, other.Owner);
        SabotageFeedback(other, SabotageVictim.InternetCenter);
        Game.Events.Eva("BuildingSabotaged", other.Owner);
        int frames = FramesSetting("SabotageDuration");
        int until = Frame + frames;
        foreach (var center in Game.ObjectsOwnedBy(other.Owner).Where(o => o.IsKindOf("FS_INTERNET_CENTER")).ToList())
            foreach (var spy in center.FindModules<SpyVisionUpdate>())
                spy.SetDisabledUntilFrame(until);
        other.SetDisabledFor("DISABLED_HACKED", frames);
        if (other.Contain is { } contain)
            foreach (var hacker in contain.Passengers.ToList())
                hacker.SetDisabledFor("DISABLED_HACKED", frames);
        return true;
    }
}

/// <summary>Saboteur on a GLA fake building: it is destroyed.</summary>
public sealed class SabotageFakeBuildingCrateCollide : CrateCollide
{
    public override bool IsValidToExecute(GameObject other) =>
        base.IsValidToExecute(other) && !other.IsEffectivelyDead && other.IsKindOf("FS_FAKE") && IsEnemy(other);

    protected override bool ExecuteCrateBehavior(GameObject other)
    {
        if (!IsGoalObject(other))
            return false;
        Game.Events.RadarEvent("INFILTRATION", other.Position, other.Owner);
        SabotageFeedback(other, SabotageVictim.FakeBuilding);
        Game.Events.Eva("BuildingSabotaged", other.Owner);
        other.Damage(new DamageInfo { DamageType = "UNRESISTABLE", DeathType = "DETONATED", SourceId = Owner.Id, Amount = other.MaxHealth });
        return true;
    }
}

/// <summary>
/// A hijacker riding inside the vehicle he took: he moves with it and shares its rank. When the
/// vehicle is gone he reappears where it was (parachuting down, ParachuteName, if it was flying).
/// </summary>
public sealed class HijackerUpdate : Module, IUpdateModule
{
    uint targetId;
    bool inVehicle, active, wasTargetAirborne;
    Vector3 ejectPos;

    public void RideIn(GameObject vehicle)
    {
        targetId = vehicle.Id;
        inVehicle = true;
        active = true;
        Owner.WakeUpdate(this);
    }

    public int Update()
    {
        if (!active || !inVehicle)
        {
            wasTargetAirborne = false;
            return 0;
        }
        if (Game.FindObject(targetId) is { IsDestroyed: false } target)
        {
            Owner.SetPosition(target.Position);
            wasTargetAirborne = target.IsSignificantlyAboveTerrain;
            ejectPos = target.Position;
            var highest = (VeterancyLevel)Math.Max((int)target.Veterancy, (int)Owner.Veterancy);
            Owner.SetVeterancy(highest);
            target.SetVeterancy(highest);
            return 0;
        }

        Owner.IsRemovedFromPartition = false;
        Owner.DrawableHidden = false;
        Owner.SetStatus("NO_COLLISIONS", false);
        Owner.SetStatus("MASKED", false);
        Owner.SetStatus("UNSELECTABLE", false);
        Owner.AI?.Stop(CommandSource.AI);
        if (wasTargetAirborne && Game.CreateObject(Setting("ParachuteName"), Owner.Owner, ejectPos) is { Contain: { } chute } && chute.IsValidContainerFor(Owner, true))
            chute.AddToContain(Owner);
        targetId = 0;
        inVehicle = false;
        active = false;
        wasTargetAirborne = false;
        return 0;
    }
}
