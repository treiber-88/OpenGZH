using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;

namespace OpenGZH.Logic.Combat;

/// <summary>
/// Turns a weapon hit into damage on objects: the direct target, plus everything within the primary
/// and secondary damage radii that the weapon's RadiusDamageAffects allows (allies, enemies, neutrals,
/// self, similar units, airborne units). Mirrors the original's damage rules.
/// </summary>
public sealed class DamageSystem
{
    readonly GameLogic game;

    static readonly int AffectsSelf = Flags.WeaponAffects.IndexOf("SELF");
    static readonly int AffectsAllies = Flags.WeaponAffects.IndexOf("ALLIES");
    static readonly int AffectsEnemies = Flags.WeaponAffects.IndexOf("ENEMIES");
    static readonly int AffectsNeutrals = Flags.WeaponAffects.IndexOf("NEUTRALS");
    static readonly int KillsSelf = Flags.WeaponAffects.IndexOf("SUICIDE");
    static readonly int NotSimilar = Flags.WeaponAffects.IndexOf("NOT_SIMILAR");
    static readonly int NotAirborne = Flags.WeaponAffects.IndexOf("NOT_AIRBORNE");

    public DamageSystem(GameLogic game) => this.game = game;

    /// <summary>Applies a weapon's damage at a position (and to <paramref name="primaryVictim"/>, if any).</summary>
    public void DealWeaponDamage(WeaponTemplate weapon, GameObject? source, GameObject? primaryVictim, Vector3 position, WeaponBonus bonus)
    {
        if (primaryVictim != null && !primaryVictim.IsDestroyed)
            position = primaryVictim.Position;

        if (weapon.HistoricBonusCount > 0)
            game.HistoricDamage.Record(weapon, source, position);

        float primaryRadius = weapon.PrimaryDamageRadius * bonus.Radius;
        float secondaryRadius = weapon.SecondaryDamageRadius * bonus.Radius;
        float primaryDamage = weapon.PrimaryDamage * bonus.Damage;
        float secondaryDamage = weapon.SecondaryDamage * bonus.Damage;
        var affects = weapon.RadiusDamageAffects;
        float radius = Math.Max(primaryRadius, secondaryRadius);

        if (radius <= 0)
        {
            if (affects.Has(KillsSelf) && source != null)
            {
                source.Damage(new DamageInfo { Amount = 1e9f, DamageType = weapon.DamageType, DeathType = weapon.DeathType, SourceId = source.Id });
                return;
            }
            if (primaryVictim != null)
                Apply(primaryVictim, primaryDamage, weapon, source);
            return;
        }

        foreach (var victim in game.ObjectsInRadius(position, radius))
        {
            if (victim.IsEffectivelyDead || victim.IsContained)
                continue;

            bool killSelf = false;
            if (source != null && victim != primaryVictim)
            {
                if (affects.Has(KillsSelf) && victim == source)
                {
                    killSelf = true;
                }
                else
                {
                    if (!affects.Has(AffectsSelf) && (victim == source || source.ProducerId == victim.Id))
                        continue;
                    if (affects.Has(NotSimilar) && victim.Template == source.Template && victim.Owner.IsAllyOf(source.Owner))
                        continue;
                    if (affects.Has(NotAirborne) && victim.IsAirborneTarget)
                        continue;

                    var relation = victim.Owner.RelationshipTo(source.Owner);
                    int required = relation switch
                    {
                        Relationship.Allies => AffectsAllies,
                        Relationship.Enemies => AffectsEnemies,
                        _ => AffectsNeutrals,
                    };
                    if (!affects.Has(required))
                        continue;
                }
            }

            // Directional damage (flamethrowers, some blasts) only hits in front of the shooter.
            if (weapon.RadiusDamageAngle < MathF.PI - 0.001f && source != null)
            {
                var toVictim = victim.Position - source.Position;
                if (toVictim.LengthSquared() > 0.0001f &&
                    Vector3.Dot(Vector3.Normalize(toVictim with { Z = 0 }), MathUtil.Forward(source.Orientation)) < MathF.Cos(weapon.RadiusDamageAngle))
                    continue;
            }

            float distance = MathUtil.Distance2D(victim.Position, position) - victim.Radius;
            float amount = killSelf ? 1e9f : distance <= primaryRadius ? primaryDamage : secondaryDamage;
            Apply(victim, amount, weapon, source);
        }
    }

    void Apply(GameObject victim, float amount, WeaponTemplate weapon, GameObject? source)
    {
        victim.Damage(new DamageInfo
        {
            Amount = amount,
            DamageType = weapon.DamageType,
            DeathType = weapon.DeathType,
            DamageStatusType = weapon.DamageStatusType.Length > 0 ? weapon.DamageStatusType : null,
            SourceId = source?.Id ?? 0,
            SourcePosition = source?.Position,
        });
    }

    /// <summary>Fires a weapon template once from a position with no shooting object (e.g. OCL "FireWeapon", historic bonuses).</summary>
    public void FireTemporaryWeapon(string weaponName, GameObject? source, Vector3 position)
    {
        var weapon = game.Catalog.Weapon(weaponName);
        if (weapon == null)
            return;
        // The original's createAndFireTempWeapon: a real weapon fired from the source (projectiles fly).
        if (source != null)
        {
            Weapon.FireTemporary(weapon, source, position);
            return;
        }
        var vet = (int)(source?.Veterancy ?? VeterancyLevel.Regular);
        game.Events.PlayFx(weapon.FireFX[vet], position);
        game.Events.PlayFx(weapon.ProjectileDetonationFX[vet], position);
        DealWeaponDamage(weapon, source, null, position, WeaponBonus.None);
    }
}

/// <summary>
/// Weapons with a "historic bonus" trigger an extra weapon when hit several times in one area within a
/// short time (e.g. several napalm strikes in one spot become a firestorm).
/// </summary>
public sealed class HistoricDamageTracker
{
    readonly GameLogic game;
    readonly Dictionary<WeaponTemplate, List<(int Frame, Vector3 Position)>> history = new();

    public HistoricDamageTracker(GameLogic game) => this.game = game;

    public void Record(WeaponTemplate weapon, GameObject? source, Vector3 position)
    {
        if (!history.TryGetValue(weapon, out var hits))
            history[weapon] = hits = new();

        int oldest = game.Frame - weapon.HistoricBonusTime;
        hits.RemoveAll(h => h.Frame < oldest);
        float radiusSquared = weapon.HistoricBonusRadius * weapon.HistoricBonusRadius;
        int nearby = hits.Count(h => MathUtil.DistanceSquared2D(h.Position, position) < radiusSquared);

        if (nearby >= weapon.HistoricBonusCount - 1 && !weapon.HistoricBonusWeapon.Equals(weapon.Name, StringComparison.OrdinalIgnoreCase))
        {
            hits.Clear();
            game.Damage.FireTemporaryWeapon(weapon.HistoricBonusWeapon, source, position);
        }
        else
        {
            hits.Add((game.Frame, position));
        }
    }
}
