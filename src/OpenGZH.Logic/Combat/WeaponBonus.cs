using OpenGZH.Core.Rules;
using OpenGZH.Logic.Core;

namespace OpenGZH.Logic.Combat;

/// <summary>Multipliers applied to a weapon by bonus conditions (veterancy, hordes, battle plans...).</summary>
public struct WeaponBonus
{
    public float Damage, Radius, Range, RateOfFire, PreAttack;

    public static WeaponBonus None => new() { Damage = 1, Radius = 1, Range = 1, RateOfFire = 1, PreAttack = 1 };
}

/// <summary>
/// A table of "CONDITION FIELD percent" lines (GameData's global WeaponBonus list, or a weapon's own).
/// As in the original, bonuses add up rather than multiply: two +25% bonuses give +50%.
/// </summary>
public sealed class WeaponBonusTable
{
    readonly List<(int Condition, string Field, float Value)> entries = new();

    public WeaponBonusTable(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            string[] words = RuleValue.Words(line);
            if (words.Length < 3)
                continue;
            int condition = Flags.WeaponBonusCondition.IndexOf(words[0]);
            if (condition >= 0)
                entries.Add((condition, words[1].ToUpperInvariant(), RuleValue.Float(words[2], 100f)));
        }
    }

    public void Append(FlagSet conditions, ref WeaponBonus bonus)
    {
        if (conditions.IsEmpty)
            return;
        foreach (var (condition, field, value) in entries)
        {
            if (!conditions.Has(condition))
                continue;
            float delta = value - 1f;
            switch (field)
            {
                case "DAMAGE": bonus.Damage += delta; break;
                case "RADIUS": bonus.Radius += delta; break;
                case "RANGE": bonus.Range += delta; break;
                case "RATE_OF_FIRE": bonus.RateOfFire += delta; break;
                case "PRE_ATTACK": bonus.PreAttack += delta; break;
            }
        }
    }
}
