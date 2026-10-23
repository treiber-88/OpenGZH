using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Scripting;

namespace OpenGZH.Logic.AI;

/// <summary>Target finding shared by hunting units and the computer player (the original's AI::findClosestEnemy).</summary>
public static class Targeting
{
    /// <summary>
    /// The nearest enemy this object can attack. With an attack priority set, the best priority wins
    /// instead, each AttackPriorityDistanceModifier of distance costing one point (never below 1);
    /// a container counts as the highest priority of what is inside it.
    /// </summary>
    public static GameObject? FindClosestEnemy(GameObject me, float range, AttackPriorityInfo? info,
        Func<GameObject, bool>? filter = null, bool attackBuildings = false)
    {
        if (!me.Weapons.HasAnyWeapon)
            return null;
        var game = me.Game;
        var candidates = new List<(GameObject Obj, float Dist)>();
        foreach (var other in game.Objects)
        {
            if (!IsLiveMapEnemy(me, other))
                continue;
            if (!attackBuildings && !AllowBuilding(me, other))
                continue;
            if (!IsPossibleToAttack(me, other))
                continue;
            if (IsStealthedAndUndetectedFor(me, other))
                continue;
            if (filter != null && !filter(other))
                continue;
            float dist = MathUtil.Distance2D(me.Position, other.Position) - me.BoundingSphereRadius - other.BoundingSphereRadius;
            dist = Math.Max(0, dist);
            if (dist > range)
                continue;
            candidates.Add((other, dist));
        }
        if (candidates.Count == 0)
            return null;
        if (info == null || game.Scripts == null || info == game.Scripts.DefaultAttackInfo)
            return candidates.MinBy(c => c.Dist).Obj;

        float modifierDistance = Math.Max(1f, game.AIData.AttackPriorityDistanceModifier);
        GameObject? best = null;
        int effectivePriority = 0, actualPriority = 0;
        foreach (var (enemy, dist) in candidates.OrderBy(c => c.Dist))
        {
            int priority = info.GetPriority(enemy.Template);
            if (priority == 0)
                continue;
            if (enemy.Contain != null)
                foreach (var passenger in enemy.Contain.Passengers)
                    priority = Math.Max(priority, info.GetPriority(passenger.Template));
            int modPriority = Math.Max(1, priority - (int)(dist / modifierDistance));
            if (modPriority > effectivePriority || (modPriority == effectivePriority && priority > actualPriority))
            {
                effectivePriority = modPriority;
                actualPriority = priority;
                best = enemy;
            }
        }
        return best;
    }

    /// <summary>PartitionFilterLiveMapEnemies: alive, an enemy, and on the same side of the map edge.</summary>
    public static bool IsLiveMapEnemy(GameObject me, GameObject other) =>
        other != me && !other.IsEffectivelyDead && !other.IsDestroyed && !other.IsRemovedFromPartition
        && other.IsOffMap == me.IsOffMap && me.RelationshipTo(other) == Relationship.Enemies;

    /// <summary>
    /// PartitionFilterRejectBuildings: the computer goes for any enemy building; a human player's units
    /// only for base defenses and garrisons that shoot back.
    /// </summary>
    static bool AllowBuilding(GameObject me, GameObject other)
    {
        if (!other.IsKindOf(Kind.Structure))
            return true;
        if (other.Owner.DefaultTeam is not { } team || me.Owner.RelationshipTo(team) != Relationship.Enemies)
            return false;
        if (!me.Owner.IsHuman)
            return true;
        if (other.IsKindOf("FS_BASE_DEFENSE"))
            return true;
        return other.Contain != null && other.Weapons.HasAnyWeapon;
    }

    /// <summary>Can one of its weapons hit the target (perhaps after moving)?</summary>
    public static bool IsPossibleToAttack(GameObject me, GameObject other)
    {
        if (other.IsKindOf(Kind.Unattackable) || other.HasStatus("MASKED") || other.IsContained)
            return false;
        return me.Weapons.ChooseFor(other) != null;
    }

    /// <summary>PartitionFilterStealthedAndUndetected (rejecting): hidden enemies, and disguised ones that fool us.</summary>
    public static bool IsStealthedAndUndetectedFor(GameObject me, GameObject other)
    {
        bool stealthed = other.HasStatus(Status.Stealthed);
        bool detected = other.HasStatus(Status.Detected);
        if (stealthed && !detected)
        {
            if (!other.IsKindOf("DISGUISER"))
                return true;
            if (other.HasStatus("DISGUISED") && other.FindModule<Modules.Stealth.StealthUpdate>() is { IsDisguised: true } stealth
                && stealth.DisguisePlayerIndex >= 0 && stealth.DisguisePlayerIndex < me.Game.Players.All.Count)
            {
                var disguisedAs = me.Game.Players.All[stealth.DisguisePlayerIndex];
                return me.Owner.RelationshipTo(disguisedAs.DefaultTeam) != Relationship.Enemies;
            }
            return false;
        }
        if (other.Contain is { Count: > 0 } contain && contain.StealthUnitsContained == contain.Count)
            return contain.Passengers.Any(p => !p.HasStatus(Status.Detected));
        return false;
    }
}
