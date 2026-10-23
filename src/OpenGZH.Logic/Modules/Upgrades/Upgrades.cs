using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Bodies;
using OpenGZH.Logic.Modules.Contain;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Upgrades;

// The upgrade modules: each waits for the upgrades in its TriggeredBy setting (see UpgradeModule)
// and then changes something about its object or its player. They mirror the original game's
// upgrade modules one for one, so the rules can keep using the same class names.

/// <summary>Switches the object to its PLAYER_UPGRADE armor set (e.g. Composite Armor, Chemical Suits).</summary>
public sealed class ArmorUpgrade : UpgradeModule
{
    protected override void OnUpgrade() => Owner.SetArmorSetFlag("PLAYER_UPGRADE");
}

/// <summary>Switches the object to its PLAYER_UPGRADE weapon set (e.g. the Humvee's TOW missile).</summary>
public sealed class WeaponSetUpgrade : UpgradeModule
{
    protected override void OnUpgrade() => Owner.SetWeaponSetFlag("PLAYER_UPGRADE");
}

/// <summary>Turns on the PLAYER_UPGRADE weapon bonus (e.g. Uranium Shells, AP Bullets).</summary>
public sealed class WeaponBonusUpgrade : UpgradeModule
{
    protected override void OnUpgrade() => Owner.SetWeaponBonus("PLAYER_UPGRADE");
}

/// <summary>
/// Replaces the object's command set. If the player or object also has the TriggerAlt upgrade,
/// CommandSetAlt is used instead (e.g. a building whose buttons depend on a second upgrade).
/// </summary>
public sealed class CommandSetUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        string alt = Setting("TriggerAlt");
        Owner.CommandSetOverride = alt.Length > 0 && Owner.HasUpgrade(alt) ? Setting("CommandSetAlt") : Setting("CommandSet");
    }
}

/// <summary>Raises (or lowers) the object's maximum health; ChangeType says what happens to current health.</summary>
public sealed class MaxHealthUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        if (Owner.Body is not { } body)
            return;
        var change = Setting("ChangeType").ToUpperInvariant() switch
        {
            "PRESERVE_RATIO" => MaxHealthChange.PreserveRatio,
            "ADD_CURRENT_HEALTH_TOO" => MaxHealthChange.AddCurrentHealthToo,
            "FULLY_HEAL" => MaxHealthChange.FullyHeal,
            _ => MaxHealthChange.SameCurrentHealth,
        };
        body.SetMaxHealth(body.MaxHealth + FloatSetting("AddMaxHealth"), change);
    }
}

/// <summary>Makes the object gain experience faster (AddXPScalar is added to its experience multiplier).</summary>
public sealed class ExperienceScalarUpgrade : UpgradeModule
{
    protected override void OnUpgrade() => Owner.ExperienceScalar += FloatSetting("AddXPScalar");
}

/// <summary>Sets one model condition (e.g. UPGRADE_ARMOR shows the extra armor plates).</summary>
public sealed class ModelConditionUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        string flag = Setting("ConditionFlag");
        if (flag.Length > 0)
            Owner.SetCondition(flag);
    }
}

/// <summary>Shows and hides named parts of the model (e.g. the Overlord's bunker or gattling cannon).</summary>
public sealed class SubObjectsUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        foreach (string name in Names("ShowSubObjects"))
            Owner.ShowSubObject(name, true);
        foreach (string name in Names("HideSubObjects"))
            Owner.ShowSubObject(name, false);
    }

    IEnumerable<string> Names(string key) => ListSetting(key).SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Sets and clears object status bits.</summary>
public sealed class StatusBitsUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        foreach (string status in Words("StatusToSet"))
            Owner.SetStatus(status);
        foreach (string status in Words("StatusToClear"))
            Owner.SetStatus(status, false);
    }

    IEnumerable<string> Words(string key) => Setting(key).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Lets the object stealth (e.g. GLA Camo Netting). The stealth module does the rest.</summary>
public sealed class StealthUpgrade : UpgradeModule
{
    protected override void OnUpgrade() => Owner.SetStatus("CAN_STEALTH");
}

/// <summary>Switches the normal locomotor set to SET_NORMAL_UPGRADED (e.g. faster Technicals with Junk Repair).</summary>
public sealed class LocomotorSetUpgrade : UpgradeModule
{
    protected override void OnUpgrade() => Owner.AI?.UpgradeLocomotors();
}

/// <summary>Runs an object creation list once (UpgradeObject), e.g. a drone attached to a vehicle.</summary>
public sealed class ObjectCreationUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        string ocl = Setting("UpgradeObject");
        if (ocl.Length > 0)
            Game.ObjectCreation.Run(ocl, Owner, Owner.Position);
    }
}

/// <summary>
/// Replaces the object with a different one in the same place, for the same team, already built
/// (e.g. a fortified version of a building).
/// </summary>
public sealed class ReplaceObjectUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        if (Game.Templates.Find(Setting("ReplaceObject")) is not { } template)
        {
            Log.Warning($"ReplaceObjectUpgrade on {Owner}: no object '{Setting("ReplaceObject")}'");
            return;
        }
        var position = Owner.Position;
        float orientation = Owner.Orientation;
        var team = Owner.Team;
        var player = Owner.Owner;
        Game.DestroyObject(Owner);

        var replacement = Game.CreateObject(template, player, position, orientation, team);
        foreach (var create in replacement.FindModules<ICreateModule>().ToList())
            create.OnBuildComplete();
    }
}

/// <summary>Lets passengers shoot out of their transport (e.g. a GLA Battle Bus).</summary>
public sealed class PassengersFireUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        if (Owner.FindModule<ContainModule>() is { } contain)
            contain.PassengersAllowedToFire = true;
    }
}

/// <summary>Gives the player a science (general's power) for free.</summary>
public sealed class GrantScienceUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        string science = Setting("GrantScience");
        if (science.Length > 0)
            Owner.Owner.GrantScience(science);
    }
}

/// <summary>Makes things of one kind cheaper (or dearer) for the player, e.g. GLA's black market.</summary>
public sealed class CostModifierUpgrade : UpgradeModule
{
    protected override void OnUpgrade() => AddToPlayer(Owner.Owner);
    protected override void AddToPlayer(Player player) => Game.Costs.AddModifier(player, Setting("EffectKindOf"), FloatSetting("Percentage"));
    protected override void RemoveFromPlayer(Player player) => Game.Costs.RemoveModifier(player, Setting("EffectKindOf"), FloatSetting("Percentage"));
}

/// <summary>A power plant upgrade (e.g. Control Rods): the plant's EnergyBonus is added to the player's power.</summary>
public sealed class PowerPlantUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        AddToPlayer(Owner.Owner);
        Owner.FindModule<PowerPlantUpdate>()?.ExtendRods();
    }

    protected override void AddToPlayer(Player player) => player.Energy.AdjustBonus(Owner.Template.EnergyBonus, adding: true);
    protected override void RemoveFromPlayer(Player player) => player.Energy.AdjustBonus(Owner.Template.EnergyBonus, adding: false);
}

/// <summary>Gives the player a radar (the mini-map). DisableProof radars keep working on low power.</summary>
public sealed class RadarUpgrade : UpgradeModule
{
    protected override void OnUpgrade()
    {
        AddToPlayer(Owner.Owner);
        Owner.FindModule<RadarUpdate>()?.ExtendRadar();
    }

    protected override void AddToPlayer(Player player)
    {
        if (BoolSetting("DisableProof"))
            player.DisableProofRadarCount++;
        else
            player.RadarCount++;
    }

    protected override void RemoveFromPlayer(Player player)
    {
        if (BoolSetting("DisableProof"))
            player.DisableProofRadarCount--;
        else
            player.RadarCount--;
    }
}

/// <summary>Animates a power plant's upgrade: POWER_PLANT_UPGRADING for RodsExtendTime, then POWER_PLANT_UPGRADED.</summary>
public sealed class PowerPlantUpdate : Module, IUpdateModule
{
    int doneFrame = -1;

    bool extended;

    /// <summary>Rods out (slowly, RodsExtendTime) or back in (at once), as the original's extendRods.</summary>
    public void ExtendRods(bool extend = true)
    {
        if (extend)
        {
            if (extended)
                return;
            Owner.SetCondition("POWER_PLANT_UPGRADING");
            extended = true;
            doneFrame = Frame + FramesSetting("RodsExtendTime");
            Owner.WakeUpdate(this);
        }
        else
        {
            Owner.SetCondition("POWER_PLANT_UPGRADING", false);
            Owner.SetCondition("POWER_PLANT_UPGRADED", false);
            extended = false;
            doneFrame = -1;
            Owner.SleepUpdate(this);
        }
    }

    public int Update()
    {
        if (doneFrame < 0)
            return LogicTime.SleepForever;
        if (Frame < doneFrame)
            return doneFrame - Frame - 1;
        Owner.SetCondition("POWER_PLANT_UPGRADING", false);
        Owner.SetCondition("POWER_PLANT_UPGRADED");
        return LogicTime.SleepForever;
    }
}

/// <summary>Animates a radar dish coming out: RADAR_EXTENDING for RadarExtendTime, then RADAR_UPGRADED.</summary>
public sealed class RadarUpdate : Module, IUpdateModule
{
    int doneFrame = -1;

    public void ExtendRadar()
    {
        Owner.SetCondition("RADAR_EXTENDING");
        doneFrame = Frame + FramesSetting("RadarExtendTime");
        Owner.WakeUpdate(this);
    }

    public int Update()
    {
        if (doneFrame < 0)
            return LogicTime.SleepForever;
        if (Frame < doneFrame)
            return doneFrame - Frame - 1;
        doneFrame = -1;
        Owner.SetCondition("RADAR_EXTENDING", false);
        Owner.SetCondition("RADAR_UPGRADED");
        return LogicTime.SleepForever;
    }
}
