using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Combat;

public enum WeaponSlot { Primary = 0, Secondary = 1, Tertiary = 2 }

public enum WeaponStatus { ReadyToFire, BetweenShots, Reloading, OutOfAmmo }

/// <summary>
/// One weapon on one object: its ammo, reload timing and firing.
/// Firing either deals damage at once, or launches a projectile object that deals it on impact.
/// </summary>
public sealed class Weapon
{
    public WeaponTemplate Template { get; }
    public WeaponSlot Slot { get; }
    public GameObject Owner { get; }

    public int AmmoInClip { get; private set; }
    public int NextShotFrame { get; private set; }
    public int LastFireFrame { get; private set; } = -1000;
    public int ShotsFiredThisBarrel { get; private set; }

    /// <summary>True while waiting for a whole clip to reload (as opposed to the delay between two shots).</summary>
    bool reloadingClip;

    public Weapon(GameObject owner, WeaponTemplate template, WeaponSlot slot)
    {
        Owner = owner;
        Template = template;
        Slot = slot;
        AmmoInClip = template.ClipSize > 0 ? template.ClipSize : int.MaxValue;
    }

    GameLogic Game => Owner.Game;

    public WeaponStatus Status
    {
        get
        {
            if (Game.Frame < NextShotFrame)
                return reloadingClip ? WeaponStatus.Reloading : WeaponStatus.BetweenShots;
            return AmmoInClip == 0 ? WeaponStatus.OutOfAmmo : WeaponStatus.ReadyToFire;
        }
    }

    public bool IsReady => Status == WeaponStatus.ReadyToFire;

    public WeaponBonus ComputeBonus()
    {
        var bonus = WeaponBonus.None;
        Game.WeaponBonuses.Append(Owner.WeaponBonusFlags, ref bonus);
        Template.ExtraBonuses.Append(Owner.WeaponBonusFlags, ref bonus);
        return bonus;
    }

    public float AttackRange => Template.AttackRange * ComputeBonus().Range;

    /// <summary>Is the target close enough to shoot (measured to the target's edge, like the original)?</summary>
    public bool IsInRange(Vector3 targetPosition, float targetRadius = 0)
    {
        float distance = MathUtil.Distance2D(Owner.Position, targetPosition) - targetRadius;
        return distance <= AttackRange && distance >= Template.MinimumAttackRange - targetRadius;
    }

    public bool IsInRange(GameObject target) => IsInRange(target.Position, target.Radius);

    public bool CanTarget(GameObject target) => Template.CanTarget(target);

    /// <summary>Fires at an object. Returns false if the weapon wasn't ready.</summary>
    public bool FireAt(GameObject target) => Fire(target, target.Position);

    /// <summary>Fires at a ground position (force-fire / artillery).</summary>
    public bool FireAtPosition(Vector3 position) => Fire(null, position);

    bool Fire(GameObject? target, Vector3 aimPoint)
    {
        if (!IsReady)
            return false;

        var bonus = ComputeBonus();
        int vet = (int)Owner.Veterancy;

        // Scatter: some weapons miss infantry or always scatter a bit.
        Vector3 impact = aimPoint;
        float scatter = Template.ScatterRadius;
        if (target != null && target.IsKindOf(Kind.Infantry))
            scatter += Template.ScatterRadiusVsInfantry;
        if (scatter > 0)
        {
            float angle = Game.Random.Float(0, MathF.Tau);
            float distance = Game.Random.Float(0, scatter);
            impact += new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * distance;
            impact.Z = Game.Terrain.GroundHeight(impact.X, impact.Y);

            // A shot that scattered off the target no longer hits it directly.
            if (target != null && MathUtil.Distance2D(impact, target.Position) > target.Radius)
                target = null;
        }

        Game.Events.WeaponFired(Owner, Template, (int)Slot);
        Game.Events.PlayFx(Template.FireFX[vet], Owner.Position, Owner);
        Game.Events.PlaySound(Template.FireSound, Owner.Position);
        if (Template.FireOCL[vet] is { Length: > 0 } fireOcl)
            Game.ObjectCreation.Run(fireOcl, Owner, Owner.Position, target);

        if (Template.ProjectileObject.Length > 0)
            LaunchProjectile(target, impact, bonus);
        else if (Template.DamageDealtAtSelfPosition)
            Game.Damage.DealWeaponDamage(Template, Owner, null, Owner.Position, bonus);
        else
            Game.Damage.DealWeaponDamage(Template, Owner, target, impact, bonus);

        ConsumeAmmo(bonus);
        Owner.SetStatus(Core.Status.IsFiringWeapon);
        return true;
    }

    void LaunchProjectile(GameObject? target, Vector3 impact, WeaponBonus bonus)
    {
        var launchPosition = Owner.Position + new Vector3(0, 0, Owner.Height * 0.6f);
        var projectile = Game.CreateObject(Template.ProjectileObject, Owner.Owner, launchPosition, MathUtil.AngleTo(Owner.Position, impact));
        if (projectile == null)
        {
            Game.Damage.DealWeaponDamage(Template, Owner, target, impact, bonus);
            return;
        }
        projectile.ProducerId = Owner.Id;
        if (projectile.FindModule<IProjectile>() is { } flight)
            flight.Launch(Owner, Template, target, impact, bonus);
        else
            Game.Damage.DealWeaponDamage(Template, Owner, target, impact, bonus);
    }

    void ConsumeAmmo(WeaponBonus bonus)
    {
        LastFireFrame = Game.Frame;
        ShotsFiredThisBarrel++;
        if (Template.ClipSize > 0)
            AmmoInClip--;

        float rate = Math.Max(0.01f, bonus.RateOfFire);
        reloadingClip = Template.ClipSize > 0 && AmmoInClip <= 0;
        if (reloadingClip)
        {
            NextShotFrame = Game.Frame + Math.Max(1, (int)(Template.ClipReloadFrames / rate));
            if (Template.AutoReloadsClip)
                AmmoInClip = Template.ClipSize;
        }
        else
        {
            int delay = Game.Random.Int(Template.MinDelayBetweenShots, Template.MaxDelayBetweenShots);
            NextShotFrame = Game.Frame + Math.Max(1, (int)(delay / rate));
        }
    }

    /// <summary>Instantly refills the clip (e.g. after docking at an airfield).</summary>
    public void Reload()
    {
        reloadingClip = false;
        AmmoInClip = Template.ClipSize > 0 ? Template.ClipSize : int.MaxValue;
        NextShotFrame = Game.Frame;
    }

    /// <summary>Weapons with AutoReloadWhenIdle refill after a pause in firing.</summary>
    public void UpdateIdleReload()
    {
        if (Template.AutoReloadWhenIdle > 0 && Template.ClipSize > 0 && AmmoInClip < Template.ClipSize
            && Game.Frame - LastFireFrame >= Template.AutoReloadWhenIdle)
            Reload();
    }

    public override string ToString() => $"{Slot} {Template.Name}";
}

/// <summary>A module that flies a projectile to its target (DumbProjectileBehavior, MissileAIUpdate, ...).</summary>
public interface IProjectile
{
    void Launch(GameObject launcher, WeaponTemplate weapon, GameObject? target, Vector3 targetPosition, WeaponBonus bonus);
}
