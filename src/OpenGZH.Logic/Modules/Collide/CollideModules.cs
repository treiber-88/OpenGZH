using System.Numerics;
using OpenGZH.Logic.Combat;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Spatial;

namespace OpenGZH.Logic.Modules.Collide;

/// <summary>
/// Infantry that tanks can run over. A crusher (CrusherLevel above 0, not an ally) driving at the
/// object kills it outright, but only if the crusher's footprint covers the object's centre and it
/// is moving towards it. Hijackers and TNT-planting Tank Hunters don't get squished by the vehicle
/// they're going for.
/// </summary>
public sealed class SquishCollide : Module, ICollideModule
{
    public void OnCollide(GameObject? other, Vector3 location, Vector3 normal)
    {
        if (other == null)
            return;
        var self = Owner;
        if (self.AI is { } ai && (ai.TargetId == other.Id || ai.EnterTargetId == other.Id))
        {
            if (self.Template.FindModule("HijackerUpdate") != null)
                return;
            if (self.Modules.OfType<SpecialPowers.SpecialAbilityUpdate>()
                    .Any(sa => sa.IsActive && sa.Power?.IsType("SPECIAL_TANKHUNTER_TNT_ATTACK") == true))
                return;
        }

        if (other.Template.CrusherLevel <= 0 || other.Owner.IsAllyOf(self.Owner))
            return;
        if (other.FindModule<Updates.PhysicsBehavior>() is not { } physics)
            return;

        // Only the middle of the soldier counts.
        var me = new Collision.Shape(self.Position, new Geometry(GeometryType.Cylinder, 1, 1, self.Geometry.Height), self.Orientation);
        var them = new Collision.Shape(other.Position, other.Geometry, other.Orientation);
        if (!Collision.Collides(them, me, out _))
            return;

        var velocity = physics.Velocity;
        var to = self.Position - other.Position;
        if (to.X * velocity.X + to.Y * velocity.Y > 0)
        {
            self.Damage(new DamageInfo { DamageType = "CRUSH", DeathType = "CRUSHED", SourceId = other.Id, Amount = DamageInfo.HugeDamage });
            self.UndetectedDefectorUntilFrame = 0;
        }
    }
}

/// <summary>
/// Fires CollideWeapon at whatever touches the object, while the object has all RequiredStatus and
/// none of ForbiddenStatus (e.g. burning debris, a GLA demo trap's trigger).
/// </summary>
public sealed class FireWeaponCollide : Module, ICollideModule, ICreateModule
{
    Weapon? weapon;
    FlagSet requiredStatus, forbiddenStatus;
    bool fireOnce;
    // As in the original, nothing ever sets this, so FireOnce has no effect; kept for faithfulness.
    bool everFired;

    protected override void Initialize()
    {
        requiredStatus = Flags.ObjectStatus.Parse(Setting("RequiredStatus"));
        forbiddenStatus = Flags.ObjectStatus.Parse(Setting("ForbiddenStatus"));
        fireOnce = BoolSetting("FireOnce");
    }

    public void OnCreate()
    {
        if (Game.Catalog.Weapon(Setting("CollideWeapon")) is { } template)
            weapon = new Weapon(Owner, template, WeaponSlot.Primary);
    }

    public void OnCollide(GameObject? other, Vector3 location, Vector3 normal)
    {
        if (other == null || weapon == null || !ShouldFire())
            return;
        weapon.Reload();
        weapon.FireAt(other);
    }

    bool ShouldFire()
    {
        var status = Owner.StatusFlags;
        if (!status.HasAll(requiredStatus))
            return false;
        if (!forbiddenStatus.IsEmpty && status.HasAny(forbiddenStatus))
            return false;
        return !(everFired && fireOnce);
    }
}
