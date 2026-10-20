using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Die;

/// <summary>Removes the object when it dies (most units have this as their final step).</summary>
public sealed class DestroyDie : DieModule
{
    protected override void Die(DamageInfo killingBlow) => Game.DestroyObject(Owner);
}

/// <summary>Runs an object creation list on death (wreckage, debris, pilots, flying turrets...).</summary>
public sealed class CreateObjectDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        string ocl = Setting("CreationList");
        var created = Game.ObjectCreation.Run(ocl, Owner, Owner.Position);
        if (BoolSetting("TransferPreviousHealth"))
            foreach (var obj in created)
                obj.Body?.SetHealth(Owner.MaxHealth > 0 ? obj.MaxHealth * Owner.Body!.MaxHealth / Owner.MaxHealth : obj.MaxHealth);
    }
}

/// <summary>Plays a visual/sound effect list on death.</summary>
public sealed class FXListDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        Game.Events.PlayFx(Setting("DeathFX"), Owner.Position, BoolSetting("OrientToObject", true) ? Owner : null);
    }
}

/// <summary>Leaves the object in the world after death (as rubble), instead of removing it.</summary>
public sealed class KeepObjectDie : DieModule, IDelaysDestruction
{
    public bool IsDelayingDestruction => true;

    protected override void Die(DamageInfo killingBlow)
    {
        Owner.SetCondition(Condition.Rubble);
        Owner.SetStatus(Core.Status.Unselectable);
        Game.Pathfinder.RemoveObstacle(Owner);
    }
}

/// <summary>
/// An instant death with effects: picks one of its FX, OCL and Weapon lines at random (each may list
/// several), then the object is removed. Used for simple deaths that need no animation.
/// </summary>
public sealed class InstantDeathBehavior : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        string? Pick(string key)
        {
            var options = ListSetting(key).SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
            return options.Count == 0 ? null : options[Game.Random.Int(0, options.Count - 1)];
        }

        if (Pick("FX") is { } fx)
            Game.Events.PlayFx(fx, Owner.Position, Owner);
        if (Pick("OCL") is { } ocl)
            Game.ObjectCreation.Run(ocl, Owner, Owner.Position);
        if (Pick("Weapon") is { } weapon)
            Game.Damage.FireTemporaryWeapon(weapon, Owner, Owner.Position);
        Game.DestroyObject(Owner);
    }
}

/// <summary>Fires a weapon when the object dies (e.g. exploding vehicles damage what's around them).</summary>
public sealed class FireWeaponWhenDeadBehavior : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        // Some only work once upgraded (StartsActive / TriggeredBy, e.g. demolition general's suicide).
        var triggers = Setting("TriggeredBy").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool startsActive = BoolSetting("StartsActive", triggers.Length == 0);
        if (!startsActive && !triggers.Any(Owner.HasUpgrade))
            return;
        Game.Damage.FireTemporaryWeapon(Setting("DeathWeapon"), Owner, Owner.Position);
    }
}

/// <summary>Grants or removes an upgrade when the object dies (e.g. "has a radar" upgrades).</summary>
public sealed class UpgradeDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        string upgrade = Setting("UpgradeToRemove").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var producer = Game.FindObject(Owner.ProducerId);
        producer?.RemoveUpgrade(upgrade);
    }
}

/// <summary>Gives the killer a crate (money, veterancy, units) when the object dies.</summary>
public sealed class CreateCrateDie : DieModule
{
    protected override void Die(DamageInfo killingBlow)
    {
        string crate = Setting("CrateData");
        if (crate.Length > 0)
            Game.Crates.TrySpawn(crate, Owner, killingBlow);
    }
}
