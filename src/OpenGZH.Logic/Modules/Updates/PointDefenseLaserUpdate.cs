using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Modules.Updates;

/// <summary>
/// The Paladin's and Avenger's point-defense laser (the original's PointDefenseLaserUpdate): every
/// ScanRate it looks within ScanRange for enemy PrimaryTargetTypes (else SecondaryTargetTypes),
/// preferring ones already within the weapon's range, and shoots WeaponTemplate at them whenever
/// the weapon's delay allows, independently of the unit's own weapons.
/// </summary>
public sealed class PointDefenseLaserUpdate : Module, IUpdateModule
{
    uint bestTargetId;
    int nextScanFrames, nextShotAvailableInFrames;
    bool inRange;
    WeaponTemplate? weaponTemplate;
    FlagSet primaryKinds, secondaryKinds;

    protected override void Initialize()
    {
        weaponTemplate = Game.Catalog.Weapon(Setting("WeaponTemplate"));
        primaryKinds = Flags.KindOf.Parse(Setting("PrimaryTargetTypes"));
        secondaryKinds = Flags.KindOf.Parse(Setting("SecondaryTargetTypes"));
        if (weaponTemplate == null)
            Log.Warning($"PointDefenseLaserUpdate on {Owner.Template.Name} has no weapon '{Setting("WeaponTemplate")}'.");
    }

    public int Update()
    {
        if (Owner.IsEffectivelyDead)
            return LogicTime.SleepForever;
        if (nextScanFrames > 0)
        {
            nextScanFrames--;
            FireWhenReady();
            return 0;
        }
        nextScanFrames = FramesSetting("ScanRate");
        if (ScanClosestTarget() != null)
            FireWhenReady();
        return 0;
    }

    void FireWhenReady()
    {
        if (weaponTemplate == null)
            return;
        var target = Game.FindObject(bestTargetId);
        if (target != null)
        {
            float fireRange = weaponTemplate.AttackRange;
            float dist = MathUtil.Distance2D(Owner.Position, target.Position);
            if (dist < fireRange)
                inRange = true;
            else if (inRange)
            {
                // It got away: look again soon.
                nextScanFrames = Game.Random.Int(0, 3);
                bestTargetId = 0;
                if (nextScanFrames == 0)
                {
                    ScanClosestTarget();
                    nextScanFrames = FramesSetting("ScanRate");
                    target = null;
                }
            }
            else
                inRange = false;
        }

        if (nextShotAvailableInFrames > 0)
        {
            nextShotAvailableInFrames--;
            return;
        }
        if (target == null || !inRange)
            return;
        if (!target.IsEffectivelyDead)
        {
            var weapon = new Weapon(Owner, weaponTemplate, WeaponSlot.Tertiary);
            weapon.Reload();
            weapon.FireAt(target);
            nextShotAvailableInFrames = Game.Random.Int(weaponTemplate.MinDelayBetweenShots, weaponTemplate.MaxDelayBetweenShots);
        }
        if (target.IsEffectivelyDead)
        {
            nextScanFrames = Game.Random.Int(0, 3);
            bestTargetId = 0;
            if (nextScanFrames == 0)
            {
                ScanClosestTarget();
                nextScanFrames = FramesSetting("ScanRate");
            }
        }
    }

    GameObject? ScanClosestTarget()
    {
        var me = Owner;
        var outOfRange = new GameObject?[2];
        var withinRange = new GameObject?[2];
        var closestDist = new float[2];
        var closestOutside = new float[2];
        float fireRange = weaponTemplate?.AttackRange ?? 0;
        float scanRange = FloatSetting("ScanRange");
        foreach (var other in Game.ObjectsInRadius(me.Position, scanRange))
        {
            int index;
            if (other.KindOf.HasAny(primaryKinds))
                index = 0;
            else if (other.KindOf.HasAny(secondaryKinds))
                index = 1;
            else
                continue;
            if (!other.IsAirborneTarget && weaponTemplate is { AntiGround: false })
                continue;
            if (me.RelationshipTo(other) != Relationship.Enemies)
                continue;
            if (other.HasStatus(Status.Stealthed) && !other.HasStatus(Status.Detected) && !other.HasStatus(Status.Disguised))
                continue;
            float dist = MathUtil.Distance2D(me.Position, other.Position);
            if (dist > scanRange)
                continue;
            if (dist <= fireRange)
            {
                if (withinRange[index] == null || dist < closestDist[index])
                {
                    closestDist[index] = dist;
                    withinRange[index] = other;
                }
            }
            else if (withinRange[index] == null)
            {
                // (The original works out a predicted position here but then measures the current one.)
                if (outOfRange[index] == null || dist < closestOutside[index])
                {
                    closestOutside[index] = dist;
                    outOfRange[index] = other;
                }
            }
        }
        foreach (var (candidate, near) in new[] { (withinRange[0], true), (withinRange[1], true), (outOfRange[0], false), (outOfRange[1], false) })
        {
            if (candidate != null)
            {
                bestTargetId = candidate.Id;
                inRange = near;
                return candidate;
            }
        }
        bestTargetId = 0;
        inRange = false;
        return null;
    }
}
