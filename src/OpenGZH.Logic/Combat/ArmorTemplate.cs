using OpenGZH.Core.Rules;

namespace OpenGZH.Logic.Combat;

/// <summary>
/// Damage multipliers per damage type, e.g. "SMALL_ARMS 25%". Types not listed use DEFAULT (100% if absent).
/// </summary>
public sealed class ArmorTemplate
{
    public string Name { get; }
    readonly Dictionary<string, float> multipliers = new(StringComparer.OrdinalIgnoreCase);
    readonly float defaultMultiplier = 1f;

    public ArmorTemplate(string name, RuleNode? rules)
    {
        Name = name;
        if (rules == null || !rules.TryGet("Armor", out var entries))
            return;

        foreach (var entry in entries.AsList())
        {
            string[] words = RuleValue.Words(entry.Value);
            if (words.Length < 2)
                continue;
            float value = RuleValue.Float(words[1], 100f);
            if (words[0].Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                defaultMultiplier = value;
            else
                multipliers[words[0]] = value;
        }
    }

    public static readonly ArmorTemplate None = new("None", null);

    /// <summary>How much of <paramref name="amount"/> gets through. UNRESISTABLE always gets through fully.</summary>
    public float AdjustDamage(string damageType, float amount)
    {
        if (damageType.Equals("UNRESISTABLE", StringComparison.OrdinalIgnoreCase))
            return amount;
        return amount * multipliers.GetValueOrDefault(damageType, defaultMultiplier);
    }
}
