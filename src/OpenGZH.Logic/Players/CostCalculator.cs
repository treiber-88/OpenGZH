using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Players;

/// <summary>
/// Works out what a player pays for something. Upgrades such as CostModifierUpgrade (e.g. cheaper
/// vehicles) register percentage changes per KindOf; they add up, like the original.
/// </summary>
public sealed class CostCalculator
{
    readonly Dictionary<Player, List<(string KindOf, float Percent)>> modifiers = new();

    public void AddModifier(Player player, string kindOf, float percent)
    {
        if (!modifiers.TryGetValue(player, out var list))
            modifiers[player] = list = new();
        list.Add((kindOf, percent));
    }

    public void RemoveModifier(Player player, string kindOf, float percent) =>
        modifiers.GetValueOrDefault(player)?.Remove((kindOf, percent));

    public int BuildCost(Player player, ObjectTemplate template)
    {
        float multiplier = 1f;
        if (modifiers.TryGetValue(player, out var list))
            foreach (var (kindOf, percent) in list)
                if (template.Is(kindOf))
                    multiplier += percent;
        return Math.Max(0, (int)MathF.Round(template.BuildCost * multiplier));
    }
}
