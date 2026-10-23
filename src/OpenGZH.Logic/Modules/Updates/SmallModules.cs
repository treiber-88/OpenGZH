using OpenGZH.Core.Rules;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// Grants UpgradeToGrant (a player or object upgrade) when the object is finished - or at creation
/// already, if ExemptStatus includes UNDER_CONSTRUCTION and it was not built (the USA command center's radar).
/// </summary>
public sealed class GrantUpgradeCreate : Module, ICreateModule
{
    bool done;

    public void OnCreate()
    {
        var exempt = Flags.ObjectStatus.Parse(Setting("ExemptStatus"));
        if (exempt.Has(Status.UnderConstruction) && !Owner.IsUnderConstruction)
            Grant();
    }

    public void OnBuildComplete()
    {
        if (done)
            return;
        done = true;
        Grant();
    }

    void Grant()
    {
        string name = Setting("UpgradeToGrant");
        var upgrade = Game.Catalog.Upgrade(name);
        if (upgrade == null)
        {
            Log.Warning($"GrantUpgradeCreate on {Owner.Template.Name}: no upgrade '{name}'.");
            return;
        }
        if (upgrade.GetString("Type", "PLAYER").Equals("OBJECT", StringComparison.OrdinalIgnoreCase))
            Owner.GiveUpgrade(name);
        else
            Owner.Owner.CompleteUpgrade(name);
    }
}

/// <summary>Shows the PREORDER model state (the pre-order bonus flag on buildings) for players who pre-ordered: nobody here.</summary>
public sealed class PreorderCreate : Module, ICreateModule
{
    public void OnCreate() { }

    public void OnBuildComplete() => Owner.SetCondition("PREORDER", false);
}

/// <summary>Units built with ScienceRequired start at StartingLevel rank (e.g. veteran Rangers with that general's power).</summary>
public sealed class VeterancyGainCreate : Module, ICreateModule
{
    public void OnCreate()
    {
        string science = Setting("ScienceRequired");
        if (science.Length > 0 && !Owner.Owner.HasScience(science))
            return;
        if (!Owner.IsTrainable)
            return;
        if (Enum.TryParse<VeterancyLevel>(Setting("StartingLevel", "REGULAR"), true, out var level))
            Owner.SetMinVeterancyLevel(level);
    }
}

/// <summary>Locks the weapon in SlotToLock once the object is finished.</summary>
public sealed class LockWeaponCreate : Module, ICreateModule
{
    public void OnCreate() { }

    public void OnBuildComplete()
    {
        var slot = Setting("SlotToLock").ToUpperInvariant() switch
        {
            "SECONDARY" or "SECONDARY_WEAPON" => WeaponSlot.Secondary,
            "TERTIARY" or "TERTIARY_WEAPON" => WeaponSlot.Tertiary,
            _ => WeaponSlot.Primary,
        };
        Owner.Weapons.LockSlot(slot);
    }
}

/// <summary>
/// Keeps firing Weapon at its own position whenever it is ready (burning debris, toxin puddles,
/// radiation): after InitialDelay, and not within ExclusiveWeaponDelay of the object's own shots.
/// </summary>
public sealed class FireWeaponUpdate : Module, IUpdateModule, ICreateModule
{
    Weapon? weapon;
    int initialDelayFrame;

    public void OnCreate()
    {
        if (Game.Catalog.Weapon(Setting("Weapon")) is { } template)
        {
            weapon = new Weapon(Owner, template, WeaponSlot.Primary);
            weapon.Reload();
        }
        initialDelayFrame = Frame + FramesSetting("InitialDelay");
    }

    public int Update()
    {
        if (Frame < initialDelayFrame || weapon == null || !weapon.IsReady || Owner.IsUnderConstruction)
            return 0;
        int exclusive = FramesSetting("ExclusiveWeaponDelay");
        if (exclusive > 0 && Frame < Owner.LastShotFiredFrame + exclusive)
            return 0;
        weapon.ForceFireAtPosition(Owner.Position);
        return 0;
    }
}

/// <summary>
/// Gives nearby friendly units (and passengers inside them) of RequiredAffectKindOf a temporary
/// weapon bonus (BonusConditionType) for BonusDuration, every BonusDelay, within BonusRange.
/// </summary>
public sealed class WeaponBonusUpdate : Module, IUpdateModule
{
    public int Update()
    {
        var required = Flags.KindOf.Parse(Setting("RequiredAffectKindOf"));
        var forbidden = Flags.KindOf.Parse(Setting("ForbiddenAffectKindOf"));
        string condition = Setting("BonusConditionType");
        int duration = FramesSetting("BonusDuration");
        float range = FloatSetting("BonusRange");
        bool Fits(GameObject o) => o.KindOf.HasAll(required) && (forbidden.IsEmpty || !o.KindOf.HasAny(forbidden));
        foreach (var obj in Game.Partition.Query(Owner.Position, range))
        {
            if (obj.IsEffectivelyDead || !(obj.Owner == Owner.Owner || Owner.Owner.IsAllyOf(obj.Owner)))
                continue;
            if (MathUtil.Distance2D(obj.Position, Owner.Position) > range)
                continue;
            if (Fits(obj))
                obj.DoTempWeaponBonus(condition, duration);
            if (obj.Contain != null)
                foreach (var passenger in obj.Contain.Passengers.Where(Fits).ToList())
                    passenger.DoTempWeaponBonus(condition, duration);
        }
        return Math.Max(0, FramesSetting("BonusDelay") - 1);
    }
}

/// <summary>Shows the ENEMYNEAR model state while an enemy is within vision range (checked every ScanDelayTime).</summary>
public sealed class EnemyNearUpdate : Module, IUpdateModule, ICreateModule
{
    bool enemyNear;
    int scanDelay;

    public void OnCreate() => scanDelay = Game.Random.Int(0, FramesSetting("ScanDelayTime"));

    public int Update()
    {
        bool wasNear = enemyNear;
        if (scanDelay == 0)
        {
            scanDelay = FramesSetting("ScanDelayTime");
            float range = Owner.VisionRange;
            enemyNear = Game.Partition.Query(Owner.Position, range).Any(o =>
                !o.IsEffectivelyDead && Owner.Owner.IsEnemyOf(o.Owner) && MathUtil.Distance2D(o.Position, Owner.Position) <= range
                && !(o.HasStatus(Status.Stealthed) && !o.HasStatus(Status.Detected)));
        }
        else
            scanDelay--;
        if (enemyNear != wasNear)
            Owner.SetCondition("ENEMYNEAR", enemyNear);
        return 0;
    }
}
