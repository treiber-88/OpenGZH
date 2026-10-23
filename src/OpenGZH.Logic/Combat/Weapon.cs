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

    /// <summary>The barrel the next shot leaves from; each fires ShotsPerBarrel shots in turn.</summary>
    int currentBarrel;
    int shotsForCurrentBarrel;

    /// <summary>Indexes into ScatterTargets not used yet this clip.</summary>
    readonly List<int> scatterTargetsUnused = new();

    void RebuildScatterTargets()
    {
        scatterTargetsUnused.Clear();
        for (int i = 0; i < Template.ScatterTargets.Count; i++)
            scatterTargetsUnused.Add(i);
    }

    /// <summary>True while waiting for a whole clip to reload (as opposed to the delay between two shots).</summary>
    bool reloadingClip;

    public Weapon(GameObject owner, WeaponTemplate template, WeaponSlot slot)
    {
        Owner = owner;
        Template = template;
        Slot = slot;
        AmmoInClip = template.ClipSize > 0 ? template.ClipSize : int.MaxValue;
        shotsForCurrentBarrel = template.ShotsPerBarrel;
        RebuildScatterTargets();
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

    /// <summary>Fires at a point whatever the range (the original's forceFireWeapon: FireWeaponUpdate).</summary>
    public bool ForceFireAtPosition(Vector3 position) => Fire(null, position);

    /// <summary>The projectile the last shot launched (if the weapon has one).</summary>
    public GameObject? LastLaunchedProjectile { get; private set; }

    /// <summary>The original's forceFireWeapon: fires at a point and hands back the projectile it launched.</summary>
    public GameObject? ForceFireWeapon(Vector3 position)
    {
        LastLaunchedProjectile = null;
        return Fire(null, position) ? LastLaunchedProjectile : null;
    }

    /// <summary>Fires a one-off weapon from an object at a point (the original's createAndFireTempWeapon: mines, demo traps).</summary>
    public static void FireTemporary(WeaponTemplate template, GameObject source, Vector3 position) =>
        new Weapon(source, template, WeaponSlot.Primary).Fire(null, position);

    bool Fire(GameObject? target, Vector3 aimPoint)
    {
        if (!IsReady)
            return false;

        // Mine-clearing weapons defuse mines, booby traps and demo traps instead of damaging them.
        if (Template.DamageType == "DISARM" && target != null)
        {
            bool cleared = false;
            int level = (int)Owner.Veterancy;
            if (target.FindModule<Modules.Behaviors.MinefieldBehavior>() is { } mine)
            {
                Game.Events.PlayFx(Template.FireFX[level], target.Position, target);
                mine.Disarm();
                cleared = true;
            }
            else if (target.IsKindOf("MINE") || target.IsKindOf("BOOBY_TRAP") || target.IsKindOf("DEMOTRAP"))
            {
                Game.Events.PlayFx(Template.FireFX[level], target.Position, target);
                Game.DestroyObject(target);
                cleared = true;
            }
            _ = cleared;
            ConsumeAmmo(ComputeBonus());
            return true;
        }

        var bonus = ComputeBonus();
        int vet = (int)Owner.Veterancy;

        // An attacking Aurora is aimed at a point behind it, as a position (so shots miss).
        if (target?.AI is Modules.AI.JetAIUpdate jet && jet.TryGetSneakyTargetingOffset(out var sneaky))
        {
            aimPoint = target.Position + sneaky;
            target = null;
        }

        int barrelCount = Game.Bones.Barrels(Owner, Slot).Count;
        if (currentBarrel >= barrelCount)
        {
            currentBarrel = 0;
            shotsForCurrentBarrel = Template.ShotsPerBarrel;
        }

        // Fixed scatter targets: each shot of the clip goes to one of the pattern's points.
        if (scatterTargetsUnused.Count > 0)
        {
            if (target != null)
            {
                aimPoint = target.Position;
                target = null;
            }
            int pick = Game.Random.Int(0, scatterTargetsUnused.Count - 1);
            var offset = Template.ScatterTargets[scatterTargetsUnused[pick]] * Template.ScatterTargetScalar;
            aimPoint = new Vector3(aimPoint.X + offset.X, aimPoint.Y + offset.Y, 0);
            aimPoint.Z = Game.Terrain.GroundHeight(aimPoint.X, aimPoint.Y);
            scatterTargetsUnused[pick] = scatterTargetsUnused[^1];
            scatterTargetsUnused.RemoveAt(scatterTargetsUnused.Count - 1);
        }

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

        if (Template.DamageType == "DEPLOY" && Owner.AI is Modules.AI.AssaultTransportAIUpdate assault)
            assault.BeginAssault(target);

        Game.Events.WeaponFired(Owner, Template, (int)Slot);
        if (Template.FireFX[vet] is { Length: > 0 } fireFx)
        {
            var fxTarget = target != null ? target.Position + new Vector3(0, 0, target.Height * 0.5f) : impact;
            Game.Events.WeaponFired(new WeaponFireFx(fireFx, Owner, (int)Slot, currentBarrel, fxTarget, Template.WeaponSpeed,
                Template.PrimaryDamageRadius, Template.IsContactWeapon, Template.PlayFxWhenStealthed));
        }
        Game.Events.PlaySound(Template.FireSound, Owner.Position, source: Owner);
        if (Template.FireOCL[vet] is { Length: > 0 } fireOcl)
            Game.ObjectCreation.Run(fireOcl, Owner, Owner.Position, target);

        if (Template.ProjectileObject.Length > 0)
            LaunchProjectile(target, impact, bonus, currentBarrel);
        else if (Template.DamageDealtAtSelfPosition)
            Game.Damage.DealWeaponDamage(Template, Owner, null, Owner.Position, bonus);
        else
            Game.Damage.DealWeaponDamage(Template, Owner, target, impact, bonus);

        if (--shotsForCurrentBarrel <= 0)
        {
            currentBarrel++;
            shotsForCurrentBarrel = Template.ShotsPerBarrel;
        }
        ConsumeAmmo(bonus);
        Owner.SetStatus(Core.Status.IsFiringWeapon);
        if (Template.RequestAssistRange > 0 && target != null)
            ProcessRequestAssistance(target);
        return true;
    }

    /// <summary>
    /// Patriot batteries (the original's processRequestAssistance): every free unit of the same kind
    /// within RequestAssistRange joins in on the target with its AssistedTargetingUpdate.
    /// </summary>
    void ProcessRequestAssistance(GameObject victim)
    {
        float rangeSq = Template.RequestAssistRange * Template.RequestAssistRange;
        foreach (var other in Game.ObjectsOwnedBy(Owner.Owner).ToList())
        {
            if (other == Owner || !other.Template.IsEquivalentTo(Owner.Template))
                continue;
            if (MathUtil.DistanceSquared2D(other.Position, Owner.Position) > rangeSq)
                continue;
            if (other.FindModule<Modules.Updates.AssistedTargetingUpdate>() is { IsFreeToAssist: true } assist)
                assist.AssistAttack(Owner, victim);
        }
    }

    void LaunchProjectile(GameObject? target, Vector3 impact, WeaponBonus bonus, int barrel)
    {
        // Launched from the barrel's launch bone, facing the way the bone points (the original's
        // positionProjectileForLaunch); without art, from part way up the launcher towards the target.
        // A passenger inside an enclosing container fires from the container's own position.
        Vector3 launchPosition;
        float yaw, pitch = 0;
        var turret = Owner.AI?.Turrets.FirstOrDefault(t => t.Controls(Slot));
        var container = Owner.IsContained ? Game.FindObject(Owner.ContainedById) : null;
        if (container?.Contain is { } contain && contain.IsEnclosingContainerFor(Owner))
        {
            launchPosition = container.Position;
            yaw = container.Orientation;
        }
        else if (Game.Bones.TryGetLaunchTransform(Owner, Slot, barrel, turret, out var launch))
        {
            launchPosition = launch.Translation;
            var forward = new Vector3(launch.M11, launch.M12, launch.M13);
            if (forward.LengthSquared() < 1e-6f)
                forward = Vector3.UnitX;
            forward = Vector3.Normalize(forward);
            yaw = MathF.Atan2(forward.Y, forward.X);
            pitch = MathF.Asin(Math.Clamp(forward.Z, -1, 1));
        }
        else
        {
            launchPosition = Owner.Position + new Vector3(0, 0, Owner.Height * 0.6f);
            yaw = MathUtil.AngleTo(Owner.Position, impact);
        }
        var projectile = Game.CreateObject(Template.ProjectileObject, Owner.Owner, launchPosition, yaw);
        LastLaunchedProjectile = projectile;
        if (projectile == null)
        {
            Game.Damage.DealWeaponDamage(Template, Owner, target, impact, bonus);
            return;
        }
        projectile.Pitch = pitch;
        projectile.ProducerId = Owner.Id;
        NewProjectileFired(projectile, target, impact);
        if (projectile.FindModule<IProjectile>() is { } flight)
        {
            flight.Launch(Owner, Template, target, impact, bonus);
            // A missile fired at a plane with countermeasures may be fooled by its flares
            // (not while the plane flies supersonic).
            if (projectile.IsKindOf("SMALL_MISSILE") && target?.FindModule<Modules.Updates.CountermeasuresBehavior>() is { IsActive: true } countermeasures
                && target.AI?.LocomotorSetName != "SET_SUPERSONIC")
                countermeasures.ReportMissile(projectile);
        }
        else
            Game.Damage.DealWeaponDamage(Template, Owner, target, impact, bonus);
    }

    uint projectileStreamId;

    /// <summary>Weapons with a ProjectileStreamName (toxin trucks) string their shots into one drawn stream (the original's newProjectileFired).</summary>
    void NewProjectileFired(GameObject projectile, GameObject? victim, Vector3 victimPos)
    {
        if (Template.ProjectileStreamName.Length == 0)
            return;
        var stream = Game.FindObject(projectileStreamId);
        if (stream == null)
        {
            projectileStreamId = 0;
            if (Game.Templates.Find(Template.ProjectileStreamName) is not { } streamTemplate)
                return;
            stream = Game.CreateObject(streamTemplate, Owner.Owner, Owner.Position, 0, Owner.Owner.DefaultTeam);
            projectileStreamId = stream.Id;
        }
        if (stream.FindModule<Modules.Updates.ProjectileStreamUpdate>() is { } update)
        {
            update.SetPosition(Owner.Position);
            update.AddProjectile(Owner.Id, projectile.Id, victim?.Id ?? 0, victim == null ? victimPos : null);
        }
    }

    void ConsumeAmmo(WeaponBonus bonus)
    {
        LastFireFrame = Game.Frame;
        ShotsFiredThisBarrel++;
        if (Template.ClipSize > 0)
            AmmoInClip--;

        float rate = Math.Max(0.01f, bonus.RateOfFire);
        reloadingClip = Template.ClipSize > 0 && AmmoInClip <= 0 && Template.AutoReloadsClip;
        if (Template.ClipSize > 0 && AmmoInClip <= 0 && !Template.AutoReloadsClip)
        {
            // Empty for good until reloaded (at an airfield, for RETURN_TO_BASE weapons).
            NextShotFrame = Game.Frame;
            return;
        }
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

    public int ClipSize => Template.ClipSize;
    public int RemainingAmmo => Template.ClipSize > 0 ? AmmoInClip : int.MaxValue;

    /// <summary>The clip reload time with rate-of-fire bonuses (veterancy, upgrades).</summary>
    public int ClipReloadFramesFor => Math.Max(1, (int)(Template.ClipReloadFrames / Math.Max(0.01f, ComputeBonus().RateOfFire)));

    /// <summary>Fills the clip to a fraction (jets reloading on the airfield); never empties it.</summary>
    public void SetClipPercentFull(float fraction)
    {
        if (Template.ClipSize <= 0)
            return;
        int ammo = (int)MathF.Floor(Template.ClipSize * fraction);
        if (ammo > AmmoInClip)
        {
            AmmoInClip = ammo;
            reloadingClip = false;
            NextShotFrame = Game.Frame;
        }
    }

    /// <summary>Instantly refills the clip (e.g. after docking at an airfield).</summary>
    public void Reload()
    {
        RebuildScatterTargets();
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

    /// <summary>
    /// The projectile touched something (null = the ground). Returns true when handled, so physics
    /// doesn't also push it around.
    /// </summary>
    bool HandleCollision(GameObject? other);
}
