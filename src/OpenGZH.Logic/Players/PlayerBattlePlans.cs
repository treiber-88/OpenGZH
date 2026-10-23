using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Players;

public enum BattlePlanStatus { None, Bombardment, HoldTheLine, SearchAndDestroy }

/// <summary>What a Strategy Center's plan does to the army (the original's BattlePlanBonuses).</summary>
public sealed class BattlePlanBonuses
{
    public float ArmorScalar = 1;
    public float SightRangeScalar = 1;
    public int Bombardment, HoldTheLine, SearchAndDestroy;
    public FlagSet ValidKindOf, InvalidKindOf;

    public BattlePlanBonuses Clone() => (BattlePlanBonuses)MemberwiseClone();
}

/// <summary>Battle plans: every Strategy Center's active plan boosts the player's qualifying units.</summary>
public sealed partial class Player
{
    const int AllPlans = 1000000;
    int bombardBattlePlans, holdTheLineBattlePlans, searchAndDestroyBattlePlans;
    BattlePlanBonuses? battlePlanBonuses;

    public int NumBattlePlansActive => bombardBattlePlans + holdTheLineBattlePlans + searchAndDestroyBattlePlans;

    public int BattlePlansActiveSpecific(BattlePlanStatus plan) => plan switch
    {
        BattlePlanStatus.Bombardment => bombardBattlePlans,
        BattlePlanStatus.HoldTheLine => holdTheLineBattlePlans,
        BattlePlanStatus.SearchAndDestroy => searchAndDestroyBattlePlans,
        _ => 0,
    };

    public bool DoesObjectQualifyForBattlePlan(GameObject obj) =>
        battlePlanBonuses != null && obj.KindOf.HasAny(battlePlanBonuses.ValidKindOf) && !obj.KindOf.HasAny(battlePlanBonuses.InvalidKindOf);

    /// <summary>The original's changeBattlePlan: the first plan of a kind adds its bonus, the last one leaving takes it away.</summary>
    public void ChangeBattlePlan(BattlePlanStatus plan, int delta, BattlePlanBonuses bonus)
    {
        bool addBonus = false, removeBonus = false;
        ref int count = ref bombardBattlePlans;
        switch (plan)
        {
            case BattlePlanStatus.Bombardment: count = ref bombardBattlePlans; break;
            case BattlePlanStatus.HoldTheLine: count = ref holdTheLineBattlePlans; break;
            case BattlePlanStatus.SearchAndDestroy: count = ref searchAndDestroyBattlePlans; break;
            default: return;
        }
        count += delta;
        if (count == 1 && delta == 1)
            addBonus = true;
        else if (count == 0 && delta == -1)
            removeBonus = true;

        if (addBonus)
            ApplyBattlePlanBonusesForPlayerObjects(bonus);
        else if (removeBonus)
        {
            // (The original turns the bonus it was given into its own inverse.)
            bonus.ArmorScalar = 1 / Math.Max(bonus.ArmorScalar, 0.01f);
            bonus.SightRangeScalar = 1 / Math.Max(bonus.SightRangeScalar, 0.01f);
            if (bonus.Bombardment > 0) bonus.Bombardment = -1;
            if (bonus.HoldTheLine > 0) bonus.HoldTheLine = -1;
            if (bonus.SearchAndDestroy > 0) bonus.SearchAndDestroy = -1;
            ApplyBattlePlanBonusesForPlayerObjects(bonus);
        }
    }

    static void ApplyBattlePlanBonusesToObject(GameObject obj, BattlePlanBonuses bonus)
    {
        var toValidate = obj;
        bool isProjectile = obj.IsKindOf(Kind.Projectile);
        if (isProjectile)
            toValidate = obj.Game.FindObject(obj.ProducerId);
        if (toValidate == null || !toValidate.KindOf.HasAny(bonus.ValidKindOf) || toValidate.KindOf.HasAny(bonus.InvalidKindOf))
            return;
        if (!isProjectile)
        {
            if (bonus.ArmorScalar != 1 && obj.Body is { } body)
                body.DamageScalar *= bonus.ArmorScalar;
            if (bonus.SightRangeScalar != 1)
            {
                obj.VisionRange *= bonus.SightRangeScalar;
                obj.ShroudClearingRange *= bonus.SightRangeScalar;
            }
        }
        obj.SetWeaponBonus("BATTLEPLAN_BOMBARDMENT", bonus.Bombardment > 0);
        obj.SetWeaponBonus("BATTLEPLAN_HOLDTHELINE", bonus.HoldTheLine > 0);
        obj.SetWeaponBonus("BATTLEPLAN_SEARCHANDDESTROY", bonus.SearchAndDestroy > 0);
    }

    public void ApplyBattlePlanBonusesForObject(GameObject obj)
    {
        if (battlePlanBonuses != null)
            ApplyBattlePlanBonusesToObject(obj, battlePlanBonuses);
    }

    public void RemoveBattlePlanBonusesForObject(GameObject obj)
    {
        if (battlePlanBonuses == null)
            return;
        var bonus = battlePlanBonuses.Clone();
        bonus.ArmorScalar = 1 / Math.Max(bonus.ArmorScalar, 0.01f);
        bonus.SightRangeScalar = 1 / Math.Max(bonus.SightRangeScalar, 0.01f);
        bonus.Bombardment = bonus.SearchAndDestroy = bonus.HoldTheLine = -AllPlans;
        ApplyBattlePlanBonusesToObject(obj, bonus);
    }

    void ApplyBattlePlanBonusesForPlayerObjects(BattlePlanBonuses bonus)
    {
        if (battlePlanBonuses == null)
            battlePlanBonuses = bonus.Clone();
        else
        {
            battlePlanBonuses.ArmorScalar *= bonus.ArmorScalar;
            battlePlanBonuses.SightRangeScalar *= bonus.SightRangeScalar;
            battlePlanBonuses.Bombardment = Math.Max(0, battlePlanBonuses.Bombardment + bonus.Bombardment);
            battlePlanBonuses.HoldTheLine = Math.Max(0, battlePlanBonuses.HoldTheLine + bonus.HoldTheLine);
            battlePlanBonuses.SearchAndDestroy = Math.Max(0, battlePlanBonuses.SearchAndDestroy + bonus.SearchAndDestroy);
        }
        foreach (var obj in Game.ObjectsOwnedBy(this).ToList())
            ApplyBattlePlanBonusesToObject(obj, bonus);
    }

    /// <summary>An object joining or leaving this player (the original's becomingTeamMember).</summary>
    internal void OnObjectJoined(GameObject obj, bool joining)
    {
        if (NumBattlePlansActive <= 0)
            return;
        if (joining)
            ApplyBattlePlanBonusesForObject(obj);
        else
            RemoveBattlePlanBonusesForObject(obj);
    }
}
